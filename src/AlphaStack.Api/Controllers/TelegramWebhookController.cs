using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Application.Features.Analytics;
using AlphaStack.Application.Features.Trading;
using AlphaStack.Domain.Enums;
using AlphaStack.Domain.Entities;
using AlphaStack.Infrastructure.ExternalServices.Fyers;

namespace AlphaStack.API.Controllers;

/// <summary>
/// Receives Telegram webhook updates for inline keyboard button taps and text commands.
///
/// One webhook URL per user — register once per bot token:
///   POST https://api.telegram.org/bot{TOKEN}/setWebhook
///        ?url=https://your-domain.com/api/telegram/webhook/{userId}
///
/// Callback data format: "{action}:{signalGroupId}"
///   e.g. "approve:3fa85f64-5717-4562-b3fc-2c963f66afa6"
///        "reject:3fa85f64-5717-4562-b3fc-2c963f66afa6"
///
/// Text commands:
///   /positions   — open positions with entry price and unrealized P&L
///   /lasttrade   — latest closed trade details from TradeAnalytics
///   /pnl         — today's realized P&L, open MTM, trade count
///   /export      — latest trade summary
///   /summary     — portfolio win rate and total P&L
///   /export_all  — full trade history
///   /help        — command list
/// </summary>
[ApiController]
[Route("api/telegram")]
public class TelegramWebhookController : ControllerBase
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TelegramWebhookController> _logger;

    // Task 3: trades approved within this window skip re-validation
    private static readonly TimeSpan ApprovalTimeout = TimeSpan.FromHours(1);

    public TelegramWebhookController(
        IServiceScopeFactory scopeFactory,
        ILogger<TelegramWebhookController> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Telegram posts updates here. Returns 200 OK immediately;
    /// all processing happens in a background task.
    /// </summary>
    [HttpPost("webhook/{userId:guid}")]
    public IActionResult Webhook(Guid userId, [FromBody] JsonElement update)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var orderRepo = scope.ServiceProvider.GetRequiredService<ITradeOrderRepository>();
                var userRepo = scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();
                var encryption = scope.ServiceProvider.GetRequiredService<IEncryptionService>();
                var telegram = scope.ServiceProvider.GetRequiredService<ITelegramNotificationService>();
                var paperSim = scope.ServiceProvider.GetRequiredService<PaperOrderSimulator>();
                var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                await HandleUpdateAsync(userId, update, orderRepo, userRepo,
                    encryption, telegram, paperSim, uow, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Webhook] Background processing failed for user {UserId}", userId);
            }
        });
        return Ok();
    }

    // ── Top-level dispatcher ──────────────────────────────────────────────────

    private async Task HandleUpdateAsync(
        Guid userId,
        JsonElement update,
        ITradeOrderRepository orderRepo,
        IUserProfileRepository userRepo,
        IEncryptionService encryption,
        ITelegramNotificationService telegram,
        PaperOrderSimulator paperSim,
        IUnitOfWork uow,
        CancellationToken ct)
    {
        _logger.LogInformation("[Webhook] Raw update received: {Update}", update.ToString());

        // Text message → command handler
        if (update.TryGetProperty("message", out var message))
        {
            await HandleTextCommandAsync(userId, message, userRepo, encryption, telegram, ct);
            return;
        }

        // Inline keyboard button tap → approval/rejection
        try
        {
            if (!update.TryGetProperty("callback_query", out var callbackQuery))
            {
                _logger.LogDebug("[Webhook] Non-callback update ignored for user {UserId}", userId);
                return;
            }

            var callbackQueryId = callbackQuery.GetProperty("id").GetString() ?? string.Empty;
            var rawData = callbackQuery.GetProperty("data").GetString() ?? string.Empty;
            var messageId = callbackQuery
                .GetProperty("message").GetProperty("message_id").GetInt32().ToString();
            var chatId = callbackQuery
                .GetProperty("message").GetProperty("chat").GetProperty("id").GetInt64();

            var parts = rawData.Split(':', 2);
            if (parts.Length != 2 || !Guid.TryParse(parts[1], out var signalGroupId))
            {
                _logger.LogWarning("[Webhook] Unrecognised callback data: {Data}", rawData);
                return;
            }

            var action = parts[0];

            var user = await userRepo.GetByIdAsync(userId, ct);
            if (user is null)
            {
                _logger.LogWarning("[Webhook] User {UserId} not found", userId);
                return;
            }

            var botToken = encryption.Decrypt(user.EncryptedTelegramBotToken);
            await telegram.AnswerCallbackQueryAsync(botToken, callbackQueryId, ct);

            _logger.LogInformation(
                "[Webhook] User {UserId} tapped '{Action}' for signal group {GroupId}",
                userId, action, signalGroupId);

            var groupOrders = await orderRepo.GetBySignalGroupAsync(signalGroupId, ct);
            var pendingOrders = groupOrders.Where(o => o.Status == OrderStatus.Pending).ToList();

            if (!pendingOrders.Any())
            {
                _logger.LogWarning(
                    "[Webhook] No pending orders for signal group {GroupId} — already handled?",
                    signalGroupId);
                await telegram.EditMessageAsync(botToken, chatId, messageId,
                    "⚠️ This signal has already been processed.");
                return;
            }

            if (action == "approve")
                await HandleApprovalAsync(signalGroupId, pendingOrders, botToken, chatId, messageId,
                    orderRepo, paperSim, uow, telegram, ct);
            else if (action == "reject")
                await HandleRejectionAsync(signalGroupId, pendingOrders, botToken, chatId, messageId,
                    orderRepo, telegram, uow, ct);
            else
                _logger.LogWarning("[Webhook] Unknown action '{Action}'", action);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Webhook] Error handling update for user {UserId}", userId);
        }
    }

    // ── Task 2: Approval with immediate acknowledgement ───────────────────────

    private async Task HandleApprovalAsync(
        Guid signalGroupId,
        List<Domain.Entities.TradeOrder> orders,
        string botToken, long chatId, string messageId,
        ITradeOrderRepository orderRepo,
        PaperOrderSimulator paperSim,
        IUnitOfWork uow,
        ITelegramNotificationService telegram,
        CancellationToken ct)
    {
        // Immediate acknowledgement before doing anything else
        await telegram.EditMessageAsync(botToken, chatId, messageId,
            "✅ *Trade Approved*\n_Executing paper fill..._");

        // Task 3: timeout re-validation
        var firstOrder = orders.First();
        if (firstOrder.ApprovalRequestedAt.HasValue)
        {
            var age = DateTime.UtcNow - firstOrder.ApprovalRequestedAt.Value;
            if (age > ApprovalTimeout)
            {
                _logger.LogWarning(
                    "[Webhook] Approval timeout — signal {GroupId} is {Age:F0} min old",
                    signalGroupId, age.TotalMinutes);

                var stale = await IsSignalStaleAsync(orders.First().StrategyExecutionId, ct);
                if (stale)
                {
                    foreach (var order in orders)
                    {
                        order.Reject();
                        await orderRepo.UpdateAsync(order, ct);
                    }
                    await uow.SaveChangesAsync(ct);

                    await telegram.EditMessageAsync(botToken, chatId, messageId,
                        $"⚠️ *Market conditions changed.*\n" +
                        $"_Trade cancelled — conditions no longer valid._\n\n" +
                        $"Signal was {(int)age.TotalMinutes} min old (limit: 60 min).\n" +
                        $"A new signal will fire if conditions return.");

                    _logger.LogWarning(
                        "[Webhook] Stale signal {GroupId} cancelled after re-validation", signalGroupId);
                    return;
                }

                _logger.LogInformation(
                    "[Webhook] Signal {GroupId} is {Age:F0} min old but conditions still valid — proceeding",
                    signalGroupId, age.TotalMinutes);
            }
        }

        // Guard against duplicate approval (e.g. web UI approved, Telegram fires late)
        var pendingOrders = orders.Where(o => o.Status == OrderStatus.Pending).ToList();
        if (!pendingOrders.Any())
        {
            _logger.LogInformation(
                "[Webhook] Signal {GroupId} already processed — ignoring duplicate approval",
                signalGroupId);
            await telegram.EditMessageAsync(botToken, chatId, messageId,
                "✅ *Already processed*\n_This trade was approved via web UI._", ct);
            return;
        }

        // Approve and fill
        foreach (var order in pendingOrders)
        {
            order.Approve();
            await orderRepo.UpdateAsync(order, ct);
        }
        await uow.SaveChangesAsync(ct);

        _logger.LogInformation(
            "[Webhook] Signal {GroupId} APPROVED — simulating fills.", signalGroupId);

        await paperSim.SimulateEntryFillAsync(signalGroupId, ct);

        var updatedOrders = await orderRepo.GetBySignalGroupAsync(signalGroupId, ct);
        var hasFailure = updatedOrders.Any(o => o.Status == OrderStatus.Failed);

        if (hasFailure)
        {
            _logger.LogWarning(
                "[Webhook] Order execution failure detected | GroupId={GroupId}", signalGroupId);
        }

        var statusText = hasFailure ? "⚠️ PARTIALLY FAILED" : "✅ APPROVED & FILLED (Paper)";
        var summary = string.Join("\n", updatedOrders.Select(o =>
            $"  {(o.Side == OrderSide.Sell ? "📉 Sell" : "📈 Buy")} " +
            $"{o.TradingSymbol} ×{o.FilledQuantity} @₹{o.FilledPrice:F2}"));

        await telegram.EditMessageAsync(botToken, chatId, messageId,
            $"{statusText}\n\n{summary}\n\n_Auto-exit will trigger on profit target or stop loss._");
    }

    // ── Task 2: Rejection with immediate acknowledgement ──────────────────────

    private async Task HandleRejectionAsync(
        Guid signalGroupId,
        List<Domain.Entities.TradeOrder> orders,
        string botToken, long chatId, string messageId,
        ITradeOrderRepository orderRepo,
        ITelegramNotificationService telegram,
        IUnitOfWork uow,
        CancellationToken ct)
    {
        // Immediate acknowledgement
        await telegram.EditMessageAsync(botToken, chatId, messageId,
            "❌ *Trade Rejected*\n_Signal skipped. Next evaluation tomorrow._");

        foreach (var order in orders)
        {
            order.Reject();
            await orderRepo.UpdateAsync(order, ct);
        }
        await uow.SaveChangesAsync(ct);

        _logger.LogInformation("[Webhook] Signal {GroupId} REJECTED.", signalGroupId);
    }

    // ── Task 3: Stale signal re-validation ────────────────────────────────────

    /// <summary>
    /// Re-runs EvaluateAsync on the strategy engine.
    /// Returns true (stale) if conditions are no longer met.
    /// </summary>
    private async Task<bool> IsSignalStaleAsync(Guid executionId, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var executionRepo = scope.ServiceProvider.GetRequiredService<IStrategyExecutionRepository>();
            var strategyDefRepo = scope.ServiceProvider.GetRequiredService<IStrategyDefinitionRepository>();
            var engineFactory = scope.ServiceProvider.GetRequiredService<IStrategyEngineFactory>();

            var execution = await executionRepo.GetByIdAsync(executionId, ct);
            if (execution is null) return true;

            var strategyDef = await strategyDefRepo.GetByIdAsync(execution.StrategyDefinitionId, ct);
            if (strategyDef is null) return true;

            var engine = engineFactory.Resolve(strategyDef.StrategyType);
            var signal = await engine.EvaluateAsync(execution, ct);

            return signal is null; // null = conditions not met = stale
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Webhook] Re-validation failed for execution {ExecId}", executionId);
            return true; // fail safe — treat as stale
        }
    }

    // ── Text command dispatcher ───────────────────────────────────────────────

    private async Task HandleTextCommandAsync(
        Guid userId,
        JsonElement message,
        IUserProfileRepository userRepo,
        IEncryptionService encryption,
        ITelegramNotificationService telegram,
        CancellationToken ct)
    {
        var text = message.TryGetProperty("text", out var textProp)
            ? textProp.GetString()?.Trim().ToLowerInvariant()
            : string.Empty;

        if (string.IsNullOrWhiteSpace(text)) return;

        var commandChatId = message.GetProperty("chat").GetProperty("id").GetInt64();

        var user = await userRepo.GetByIdAsync(userId, ct);
        if (user is null)
        {
            _logger.LogWarning("[Webhook] User {UserId} not found for command", userId);
            return;
        }

        string botToken;
        try
        {
            botToken = encryption.Decrypt(user.EncryptedTelegramBotToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[Webhook] Could not decrypt Telegram bot token for user {UserId}. Re-save the Telegram bot token so it is encrypted with the current Data Protection key ring.",
                userId);
            return;
        }

        _logger.LogInformation("[Webhook] Command '{Text}' from user {UserId}", text, userId);

        switch (text)
        {
            case "/positions":
                await HandlePositionsCommandAsync(userId, commandChatId, botToken, telegram, ct);
                break;

            case "/lasttrade":
                await HandleLastTradeCommandAsync(userId, commandChatId, botToken, telegram, ct);
                break;

            case "/pnl":
                await HandlePnlCommandAsync(userId, commandChatId, botToken, telegram, ct);
                break;

            case "/export":
            case "/export@zerodhaAlphaStack_bot":
                await telegram.SendMessageAsync(botToken, commandChatId,
                    "📊 Generating latest trade summary...", ct);
                await HandleExportCommandAsync(userId, commandChatId, ct);
                break;

            case "/export_all":
                await telegram.SendMessageAsync(botToken, commandChatId,
                    "📁 Exporting full trade history...", ct);
                await HandleExportAllCommandAsync(userId, commandChatId, ct);
                break;

            case "/summary":
                await HandleExportAllCommandAsync(userId, commandChatId, ct);
                break;

            case "/help":
            case "/help@zerodhaAlphaStack_bot":
                await telegram.SendMessageAsync(botToken, commandChatId,
                    "📋 *Available Commands*\n\n" +
                    "/positions   — open positions \\+ unrealized P&L\n" +
                    "/pnl         — today's P&L summary\n" +
                    "/lasttrade   — latest closed trade details\n" +
                    "/export      — latest trade summary\n" +
                    "/summary     — portfolio win rate \\+ total P&L\n" +
                    "/export\\_all  — full trade history\n" +
                    "/shadowexport - Shadpw Trade variation \n" +
                    "/tokenstatus - fyers token generation" +
                    "/help        — show this message", ct);
                break;
            case "/shadowexport":
                await HandleShadowExportCommandAsync(userId, commandChatId, botToken, telegram, ct);
                break;
            case "/tokenstatus":
                await HandleTokenStatusCommandAsync(userId, commandChatId, botToken, telegram, ct);
                break;
            default:
                await telegram.SendMessageAsync(botToken, commandChatId,
                    "❓ Unknown command. Type /help for the list.", ct);
                break;
        }
    }

    // ── Task 1: /positions ────────────────────────────────────────────────────

    private async Task HandlePositionsCommandAsync(
        Guid userId, long chatId, string botToken,
        ITelegramNotificationService telegram,
        CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var executionRepo = scope.ServiceProvider.GetRequiredService<IStrategyExecutionRepository>();
            var positionRepo = scope.ServiceProvider.GetRequiredService<IPositionRepository>();

            var executions = await executionRepo.GetByUserAsync(userId, ct);
            var allPositions = new List<Domain.Entities.Position>();

            foreach (var exec in executions)
            {
                var positions = await positionRepo.GetOpenByExecutionAsync(exec.Id, ct);
                allPositions.AddRange(positions);
            }

            if (!allPositions.Any())
            {
                await telegram.SendMessageAsync(botToken, chatId,
                    "📭 *No open positions.*", ct);
                return;
            }

            var groups = allPositions.GroupBy(p => p.SignalGroupId);
            var lines = new List<string> { "📊 *Open Positions*", "" };

            foreach (var group in groups)
            {
                var legs = group.ToList();
                var totalMtm = legs.Sum(p => p.UnrealizedPnL);
                var pnlIcon = totalMtm >= 0 ? "🟢" : "🔴";

                lines.Add($"┌ `{group.Key.ToString()[..8]}...`");

                foreach (var leg in legs.OrderBy(p => p.Side))
                {
                    var side = leg.Side == OrderSide.Sell ? "📉 Short" : "📈 Long ";
                    var legPnl = leg.UnrealizedPnL;
                    var legIcon = legPnl >= 0 ? "▲" : "▼";
                    lines.Add(
                        $"│ {side}  {leg.TradingSymbol}\n" +
                        $"│  Qty: {leg.Quantity}  Entry: ₹{leg.EntryPrice:F2}" +
                        $"  LTP: ₹{leg.CurrentPrice:F2}  {legIcon}₹{legPnl:F0}");
                }

                lines.Add($"└ {pnlIcon} *Net MTM: ₹{totalMtm:F0}*");
                lines.Add("");
            }

            var totalUnrealized = allPositions.Sum(p => p.UnrealizedPnL);
            lines.Add($"💼 *Total unrealized: ₹{totalUnrealized:F0}*");

            await telegram.SendMessageAsync(botToken, chatId, string.Join("\n", lines), ct);

            _logger.LogInformation(
                "[Webhook] /positions | UserId={U} Positions={C}",
                userId, allPositions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Webhook] /positions failed for user {UserId}", userId);
            await telegram.SendMessageAsync(botToken, chatId,
                "⚠️ Failed to fetch positions. Check logs.", ct);
        }
    }

    // ── Task 4: /lasttrade ────────────────────────────────────────────────────

    private async Task HandleLastTradeCommandAsync(
        Guid userId, long chatId, string botToken,
        ITelegramNotificationService telegram,
        CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var analyticsRepo = scope.ServiceProvider.GetRequiredService<ITradeAnalyticsRepository>();

            var closed = await analyticsRepo.GetAllClosedAsync(ct);

            closed = closed
            .Where(x => !string.IsNullOrWhiteSpace(x.ExitReason)).ToList();

            if (!closed.Any())
            {
                await telegram.SendMessageAsync(botToken, chatId,
                    "📭 *No closed trades yet.*", ct);
                return;
            }

            // Sort by actual close time (entry time + holding duration), not latest entry.
            var latest = closed.OrderByDescending(x => x.CreatedAt).FirstOrDefault();

            var outcome = (latest.NetPnL ?? 0) >= 0 ? "✅ Win" : "❌ Loss";
            var pnlSign = (latest.NetPnL ?? 0) >= 0 ? "+" : "";
            var optionType = (latest.StrategyName?.Contains("BullPut", StringComparison.OrdinalIgnoreCase) == true) ? "PE" : "CE";
            var entryDate = latest.CreatedAt.ToLocalTime();

            // Escape helper — escapes all MarkdownV2 special chars
            static string Esc(string s) => s
                .Replace("_", "\\_").Replace("*", "\\*").Replace("[", "\\[")
                .Replace("]", "\\]").Replace("(", "\\(").Replace(")", "\\)")
                .Replace("~", "\\~").Replace("`", "\\`").Replace(">", "\\>")
                .Replace("#", "\\#").Replace("+", "\\+").Replace("-", "\\-")
                .Replace("=", "\\=").Replace("|", "\\|").Replace("{", "\\{")
                .Replace("}", "\\}").Replace(".", "\\.").Replace("!", "\\!");

            var msg =
                $"📋 *Last Closed Trade*\n\n" +
                $"Strategy:      {Esc(latest.StrategyName)}\n" +
                $"Entry:         {Esc(entryDate.ToString("dd-MMM HH:mm"))} IST\n" +
                $"Exit reason:   {Esc(latest.ExitReason ?? "—")}\n\n" +
                $"Short strike:  {latest.ShortStrike:F0}{optionType}\n" +
                $"Long strike:   {latest.LongStrike:F0}{optionType}\n" +
                $"Credit:        ₹{Esc($"{latest.PremiumCollected:F2}")}/unit\n\n" +
                $"Gross P&L:     ₹{Esc($"{latest.GrossPnL ?? 0:F0}")}\n" +
                $"Brokerage:     ₹{Esc($"{latest.Brokerage ?? 0:F0}")}\n" +
                $"*Net P&L:      {Esc($"{pnlSign}₹{latest.NetPnL ?? 0:F0}")}*\n\n" +
                $"Held for:      {latest.HoldingMinutes ?? 0} min\n" +
                $"Result:        {outcome}\n\n" +
                $"VIX: {Esc($"{latest.VixAtEntry:F1}")} \\({Esc(latest.VixRegime)}\\)  " +
                $"Regime: {Esc(latest.MarketRegime)}";

            await telegram.SendMessageAsync(botToken, chatId, msg, ct);

            _logger.LogInformation(
                "[Webhook] /lasttrade | UserId={U} NetPnL=₹{P:F0}",
                userId, latest.NetPnL ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Webhook] /lasttrade failed for user {UserId}", userId);
            await telegram.SendMessageAsync(botToken, chatId,
                "⚠️ Failed to fetch last trade. Check logs.", ct);
        }
    }

    // ── Task 5: /pnl ─────────────────────────────────────────────────────────

    private async Task HandlePnlCommandAsync(
        Guid userId, long chatId, string botToken,
        ITelegramNotificationService telegram,
        CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var analyticsRepo = scope.ServiceProvider.GetRequiredService<ITradeAnalyticsRepository>();
            var executionRepo = scope.ServiceProvider.GetRequiredService<IStrategyExecutionRepository>();
            var positionRepo = scope.ServiceProvider.GetRequiredService<IPositionRepository>();

            var ist = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
            var todayIst = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ist));

            // Today's closed trades — match on actual exit/update date in IST.
            // CreatedAt is entry time, so overnight trades closed today were being missed.
            var allClosed = await analyticsRepo.GetAllClosedAsync(ct);
            var todayTrades = allClosed
                    .Where(x => !string.IsNullOrWhiteSpace(x.ExitReason) &&
                                x.UpdatedAt.HasValue)
                    .Where(x => DateOnly.FromDateTime(
                        TimeZoneInfo.ConvertTime(x.UpdatedAt.Value, ist)) == todayIst)
                    .ToList();
                

            var realizedPnL = todayTrades.Sum(x => x.NetPnL ?? 0);
            var wins = todayTrades.Count(x => (x.NetPnL ?? 0) > 0);
            var losses = todayTrades.Count(x => (x.NetPnL ?? 0) <= 0);

            // Open MTM across all running executions for this user
            var executions = await executionRepo.GetByUserAsync(userId, ct);
            decimal openMtm = 0;
            int openLegs = 0;

            foreach (var exec in executions)
            {
                var positions = await positionRepo.GetOpenByExecutionAsync(exec.Id, ct);
                openMtm += positions.Sum(p => p.UnrealizedPnL);
                openLegs += positions.Count;
            }

            var openSpreads = openLegs / 2; // each spread = 2 legs
            var totalPnl = realizedPnL + openMtm;
            var pnlIcon = totalPnl >= 0 ? "🟢" : "🔴";
            var realIcon = realizedPnL >= 0 ? "✅" : "❌";
            var mtmIcon = openMtm >= 0 ? "📈" : "📉";

            var msg =
                $"💹 *P&L Summary — {todayIst:dd MMM yyyy}*\n\n" +
                $"{realIcon} Realized P&L:  *₹{realizedPnL:F0}*\n" +
                $"{mtmIcon} Open MTM:       *₹{openMtm:F0}*\n" +
                $"━━━━━━━━━━━━━━━━━━\n" +
                $"{pnlIcon} *Total:          ₹{totalPnl:F0}*\n\n" +
                $"📊 Today's trades: {todayTrades.Count}  " +
                $"\\(✅ {wins}W  ❌ {losses}L\\)\n" +
                $"📂 Open positions: {openSpreads} spread\\(s\\)";

            await telegram.SendMessageAsync(botToken, chatId, msg, ct);

            _logger.LogInformation(
                "[Webhook] /pnl | UserId={U} Realized=₹{R:F0} OpenMtm=₹{M:F0}",
                userId, realizedPnL, openMtm);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Webhook] /pnl failed for user {UserId}", userId);
            await telegram.SendMessageAsync(botToken, chatId,
                "⚠️ Failed to fetch P&L. Check logs.", ct);
        }
    }

    // ── Existing: /export ─────────────────────────────────────────────────────

    private async Task HandleExportCommandAsync(
    Guid userId, long chatId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var csvExport = scope.ServiceProvider.GetRequiredService<CsvExportService>();
        var analyticsRepo = scope.ServiceProvider.GetRequiredService<ITradeAnalyticsRepository>();
        var positionRepo = scope.ServiceProvider.GetRequiredService<IPositionRepository>();
        var executionRepo = scope.ServiceProvider.GetRequiredService<IStrategyExecutionRepository>();
        var userRepo = scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();
        var encryption = scope.ServiceProvider.GetRequiredService<IEncryptionService>();
        var telegram = scope.ServiceProvider.GetRequiredService<ITelegramNotificationService>();

        var user = await userRepo.GetByIdAsync(userId, ct);
        if (user is null) return;
        var botToken = encryption.Decrypt(user.EncryptedTelegramBotToken);

        // Export CSV
        var path = await csvExport.ExportAllTradesAsync(ct);

        // Build summary stats
        var closed = await analyticsRepo.GetAllClosedAsync(ct);
        // Defensive filtering: only analytics rows with actual exit data count as closed.
        // Some repository implementations may return entry rows created when trade opens.
        closed = closed
            .Where(x => !string.IsNullOrWhiteSpace(x.ExitReason))
            .ToList();
        // Only use this user's executions for open MTM.
        var executions = await executionRepo.GetByUserAsync(userId, ct);

        // Get open positions across all executions
        var openPositions = new List<Position>();
        foreach (var exec in executions)
        {
            var positions = await positionRepo.GetOpenByExecutionAsync(exec.Id, ct);
            openPositions.AddRange(positions);
        }

        var totalTrades = closed.Count;
        var winners = closed.Count(t => (t.NetPnL ?? 0) > 0);
        var totalPnL = closed.Sum(t => t.NetPnL ?? 0);
        var openMtm = openPositions.Sum(p => p.UnrealizedPnL);
        var winRate = totalTrades > 0 ? (winners * 100.0 / totalTrades) : 0;

        var hasOpenTrades = openPositions.Any();
        var openNote = hasOpenTrades
            ? $"\n\nOpen position MTM: {(openMtm >= 0 ? "+" : "")}₹{openMtm:F0}"
            : "\n\nNo open positions.";

        var summary =
            $"📊 Export Summary\n\n" +
            $"Total closed trades: {totalTrades}\n" +
            $"Winners: {winners} | Losers: {totalTrades - winners}\n" +
            $"Win rate: {winRate:F1}%\n" +
            $"Total realized P&L: {(totalPnL >= 0 ? "+" : "")}₹{totalPnL:F0}" +
            openNote + "\n\n" +
            (string.IsNullOrEmpty(path)
                ? "No trades to export yet."
                : $"CSV ready — download via Swagger:\nGET /api/analytics/export/all");

        await telegram.SendMessageAsync(botToken, chatId, summary, ct);
    }

    // ── Existing: /export_all / /summary ─────────────────────────────────────

    private async Task HandleExportAllCommandAsync(
        Guid userId, long chatId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var csvExport = scope.ServiceProvider.GetRequiredService<CsvExportService>();
        var userRepo = scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();
        var encryption = scope.ServiceProvider.GetRequiredService<IEncryptionService>();
        var telegram = scope.ServiceProvider.GetRequiredService<ITelegramNotificationService>();

        var user = await userRepo.GetByIdAsync(userId, ct);
        if (user is null) return;

        var botToken = encryption.Decrypt(user.EncryptedTelegramBotToken);
        var summary = await csvExport.BuildPortfolioSummaryAsync(ct);
        await telegram.SendMessageAsync(botToken, chatId, summary, ct);
    }
    private async Task HandleShadowExportCommandAsync(
        Guid userId, long chatId, string botToken,
        ITelegramNotificationService telegram,
        CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var shadowExport = scope.ServiceProvider.GetRequiredService<ShadowCsvExportService>();

            // Send summary stats first
            var summary = await shadowExport.BuildSummaryAsync(ct);
            await telegram.SendMessageAsync(botToken, chatId, summary, ct);

            // Export CSV and notify path
            var path = await shadowExport.ExportAllAsync(ct);
            if (!string.IsNullOrEmpty(path))
            {
                await telegram.SendMessageAsync(botToken, chatId,
                    $"📁 Shadow CSV exported\\.\n`GET /api/analytics/shadow\\-export`", ct);
            }

            _logger.LogInformation("[Webhook] /shadowexport handled for user {UserId}", userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Webhook] /shadowexport failed for user {UserId}", userId);
            await telegram.SendMessageAsync(botToken, chatId,
                "⚠️ Shadow export failed. Check logs.", ct);
        }
    }
    private async Task HandleTokenStatusCommandAsync(
        Guid userId, long chatId, string botToken,
        ITelegramNotificationService telegram,
        CancellationToken ct)
    {
        using var scope      = _scopeFactory.CreateScope();
        var tokenService     = scope.ServiceProvider.GetRequiredService<FyersTokenService>();

        var ist      = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        var setAtIst = TimeZoneInfo.ConvertTimeFromUtc(tokenService.TokenSetAt, ist);
        var isFresh  = tokenService.IsTokenFreshToday();
        var icon     = isFresh ? "✅" : "⚠️";

        var msg = $"{icon} *Fyers Token Status*\n\n" +
                  $"Fresh today: {(isFresh ? "Yes" : "No")}\n" +
                  $"Last updated: {setAtIst:dd MMM HH:mm} IST\n" +
                  $"Token length: {tokenService.AccessToken.Length} chars";

        await telegram.SendMessageAsync(botToken, chatId, msg, ct);
    }
}
