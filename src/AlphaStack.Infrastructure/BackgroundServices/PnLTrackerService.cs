using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Application.Features.Trading;
using AlphaStack.Domain.Enums;
using AlphaStack.Domain.Entities;
using AlphaStack.Infrastructure.ExternalServices.Fyers;


namespace AlphaStack.Infrastructure.BackgroundServices;

/// <summary>
/// Two-speed cycle design:
///   Fast (1 min)  — LTP refresh + exit evaluation for real open positions
///   Slow (5 min)  — ShadowExitSimulatorJob + PaperTradeExpiryCloserJob + EOD summary
///
/// Runs during NSE market hours (9:15 – 15:30 IST) on weekdays.
/// </summary>
public class PnLTrackerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FyersTokenService _tokenService;
    private readonly ILogger<PnLTrackerService> _logger;

    private static readonly TimeZoneInfo Ist =
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    private static readonly TimeOnly MarketOpen  = new(9, 15);
    private static readonly TimeOnly MarketClose = new(15, 30);
    private static readonly TimeOnly EodSummaryTime = new(15, 0);

    private static readonly TimeSpan FastInterval = TimeSpan.FromMinutes(1);

    private DateOnly _lastEodDate = DateOnly.MinValue;
    private DateTime _lastSlowCycle = DateTime.MinValue;

    // Shared between fast and slow cycles for EOD open-position snapshot
    private List<Position> _lastAllOpenPositions = new();

    public PnLTrackerService(
        IServiceScopeFactory scopeFactory,
        FyersTokenService tokenService,
        ILogger<PnLTrackerService> logger)
    {
        _scopeFactory = scopeFactory;
        _tokenService = tokenService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[PnLTracker] Service started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var istNow   = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Ist);
                var timeNow  = TimeOnly.FromDateTime(istNow);

                if (IsMarketHours(timeNow) && IsWeekday(istNow))
                {
                    // ── Fast cycle (1 min) — real position tracking ────────────
                    await RunTrackerCycleAsync(stoppingToken);

                    // ── Slow cycle (5 min) — shadow + expiry + EOD ────────────
                    if ((DateTime.UtcNow - _lastSlowCycle).TotalMinutes >= 5)
                    {
                        await RunSlowCycleAsync(stoppingToken);
                        _lastSlowCycle = DateTime.UtcNow;
                    }
                }
                else
                {
                    _logger.LogDebug("[PnLTracker] Outside market hours ({Time} IST). Sleeping.", timeNow);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PnLTracker] Unhandled error in tracker cycle.");
            }

            using var wakeCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var delayTask   = Task.Delay(FastInterval, wakeCts.Token);
            var refreshTask = _tokenService.WaitForTokenRefreshAsync(wakeCts.Token);
            var completed   = await Task.WhenAny(delayTask, refreshTask);
            await wakeCts.CancelAsync();

            if (stoppingToken.IsCancellationRequested) break;

            if (completed == refreshTask)
                _logger.LogInformation("[PnLTracker] Fyers token refreshed — running next cycle now.");
        }

        _logger.LogInformation("[PnLTracker] Service stopped.");
    }

    // ── Fast cycle — real positions only ─────────────────────────────────────

    private async Task RunTrackerCycleAsync(CancellationToken ct)
    {
        var spotCache        = new Dictionary<string, Quote>();
        var optionQuoteCache = new Dictionary<string, Quote>();

        using var scope = _scopeFactory.CreateScope();
        var executionRepo   = scope.ServiceProvider.GetRequiredService<IStrategyExecutionRepository>();
        var positionRepo    = scope.ServiceProvider.GetRequiredService<IPositionRepository>();
        var strategyDefRepo = scope.ServiceProvider.GetRequiredService<IStrategyDefinitionRepository>();
        var engineFactory   = scope.ServiceProvider.GetRequiredService<IStrategyEngineFactory>();
        var signalProcessor = scope.ServiceProvider.GetRequiredService<SignalProcessor>();
        var analyticsRepo   = scope.ServiceProvider.GetRequiredService<ITradeAnalyticsRepository>();
        var marketData      = scope.ServiceProvider.GetRequiredService<IMarketDataProvider>();
        var uow             = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var istNow            = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Ist);
        var runningExecutions = await executionRepo.GetRunningExecutionsAsync(ct);

        _logger.LogInformation(
            "[PnLTracker] Cycle start — {Count} running executions.", runningExecutions.Count);

        var allOpenPositions = new List<Position>();

        foreach (var execution in runningExecutions)
        {
            try
            {
                var strategyDef = await strategyDefRepo.GetByIdAsync(execution.StrategyDefinitionId, ct);
                if (strategyDef is null) continue;

                var engine = engineFactory.Resolve(strategyDef.StrategyType);

                // ── Check exit conditions ──────────────────────────────────────
                var exitSignal = await engine.EvaluateExitAsync(execution, ct);
                if (exitSignal is not null)
                {
                    _logger.LogInformation(
                        "[PnLTracker] Exit signal for execution {ExecId} ({Strategy})",
                        execution.Id, strategyDef.StrategyType);
                    await signalProcessor.ProcessAsync(exitSignal, ct);
                }

                // ── Update unrealized P&L ──────────────────────────────────────
                var openPositions = await positionRepo.GetOpenByExecutionAsync(execution.Id, ct);
                allOpenPositions.AddRange(openPositions);

                // ── Refresh live LTP for each open leg ─────────────────────────
                foreach (var pos in openPositions)
                {
                    await Task.Delay(250, ct);
                    try
                    {
                        var quoteKey = $"{pos.Exchange}:{pos.TradingSymbol}";

                        if (!optionQuoteCache.TryGetValue(quoteKey, out var quote))
                        {
                            quote = await marketData.GetQuoteAsync(
                                pos.TradingSymbol,
                                pos.Exchange.ToString(),
                                ct);

                            if (quote is not null)
                                optionQuoteCache[quoteKey] = quote;
                        }
                        if (quote is not null)
                        {
                            pos.UpdateCurrentPrice(quote.LastPrice);
                            _logger.LogInformation(
                                "[PnLTracker] LTP updated | {Symbol} Entry=₹{Entry:F2} LTP=₹{Ltp:F2}",
                                pos.TradingSymbol,
                                pos.EntryPrice,
                                quote.LastPrice);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "[PnLTracker] Failed to refresh LTP for {Symbol}", pos.TradingSymbol);
                    }
                }

                 // ── End LTP refresh ────────────────────────────────────────────────────────

                var unrealizedPnL = openPositions.Sum(p => p.UnrealizedPnL);
                execution.UpdateUnrealizedPnL(unrealizedPnL);
                await executionRepo.UpdateAsync(execution, ct);

                // ── MTM update on analytics ────────────────────────────────────
                if (openPositions.Any())
                {
                    var signalGroupId = openPositions.First().SignalGroupId;
                    try
                    {
                        var analytics = await analyticsRepo.GetByTradeIdAsync(signalGroupId, ct);
                        if (analytics is not null)
                        {
                            analytics.UpdateMtm(unrealizedPnL);

                            // Task 4: check if spot has touched or crossed the short strike
                            var shortLegPos = openPositions.FirstOrDefault(p => p.Side == OrderSide.Sell);
                            if (shortLegPos?.StrikePrice.HasValue == true)
                            {
                                // Fetch current Nifty spot for strike breach check
                                try
                                {
                                    var spotSymbol = shortLegPos.TradingSymbol.StartsWith("FINNIFTY", StringComparison.OrdinalIgnoreCase)
                                        ? "NIFTY FIN SERVICE"
                                        : "NIFTY 50";

                                    if (!spotCache.TryGetValue(spotSymbol, out var spotQ))
                                    {
                                        spotQ = await marketData.GetQuoteAsync(spotSymbol, "NSE", ct);
                                        if (spotQ is not null)
                                            spotCache[spotSymbol] = spotQ;
                                    }

                                    if (spotQ is not null)
                                    {
                                        var strategyType = strategyDef.StrategyType;
                                        var isIronCondor = strategyType.Contains("IronCondor");
                                        var isBullPut = strategyType.Contains("BullPut");

                                        bool strikeBreached;

                                        if (isIronCondor)
                                        {
                                            var putShort = openPositions
                                                .FirstOrDefault(p => p.Side == OrderSide.Sell &&
                                                    p.TradingSymbol.EndsWith("PE", StringComparison.OrdinalIgnoreCase));
                                            var callShort = openPositions
                                                .FirstOrDefault(p => p.Side == OrderSide.Sell &&
                                                    p.TradingSymbol.EndsWith("CE", StringComparison.OrdinalIgnoreCase));

                                            var putBreached = putShort?.StrikePrice.HasValue == true &&
                                                               spotQ.LastPrice <= putShort.StrikePrice.Value;
                                            var callBreached = callShort?.StrikePrice.HasValue == true &&
                                                               spotQ.LastPrice >= callShort.StrikePrice.Value;

                                            strikeBreached = putBreached || callBreached;

                                            if (putBreached)
                                                _logger.LogWarning(
                                                    "[PnLTracker] Put short strike TOUCHED | Spot={Spot:F0} <= PutStrike={Strike:F0} | GroupId={G}",
                                                    spotQ.LastPrice, putShort!.StrikePrice!.Value, signalGroupId);

                                            if (callBreached)
                                                _logger.LogWarning(
                                                    "[PnLTracker] Call short strike TOUCHED | Spot={Spot:F0} >= CallStrike={Strike:F0} | GroupId={G}",
                                                    spotQ.LastPrice, callShort!.StrikePrice!.Value, signalGroupId);
                                        }
                                        else if (isBullPut)
                                        {
                                            // Danger: spot drops below short put
                                            strikeBreached = shortLegPos?.StrikePrice.HasValue == true &&
                                                             spotQ.LastPrice <= shortLegPos.StrikePrice.Value;

                                            if (strikeBreached)
                                                _logger.LogWarning(
                                                    "[PnLTracker] Short strike TOUCHED | Spot={Spot:F0} <= PutStrike={Strike:F0} | GroupId={G}",
                                                    spotQ.LastPrice, shortLegPos!.StrikePrice!.Value, signalGroupId);
                                        }
                                        else
                                        {
                                            // BearCallSpread
                                            strikeBreached = shortLegPos?.StrikePrice.HasValue == true &&
                                                             spotQ.LastPrice >= shortLegPos.StrikePrice.Value;

                                            if (strikeBreached)
                                                _logger.LogWarning(
                                                    "[PnLTracker] Short strike TOUCHED | Spot={Spot:F0} >= CallStrike={Strike:F0} | GroupId={G}",
                                                    spotQ.LastPrice, shortLegPos!.StrikePrice!.Value, signalGroupId);
                                        }

                                        if (strikeBreached)
                                            analytics.MarkShortStrikeTouched();
                                    }
                                }
                                catch (Exception spotEx)
                                {
                                    _logger.LogDebug(spotEx,
                                        "[PnLTracker] Strike breach spot fetch failed for {G}", signalGroupId);
                                }
                            }

                            await analyticsRepo.UpdateAsync(analytics, ct);
                            _logger.LogDebug(
                                "[PnLTracker] MTM updated | GroupId={G} PnL=₹{P:F0} MaxProfit=₹{MP:F0} MaxLoss=₹{ML:F0}",
                                signalGroupId, unrealizedPnL,
                                analytics.MaxMtmProfit, analytics.MaxMtmLoss);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "[PnLTracker] MTM analytics update failed for group {G}", signalGroupId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PnLTracker] Error processing execution {ExecId}", execution.Id);
            }
        }

        // Update shared snapshot for EOD summary
        _lastAllOpenPositions = allOpenPositions;

        await uow.SaveChangesAsync(ct);
        _logger.LogInformation("[PnLTracker] Cycle complete.");
    }

    // ── Slow cycle — shadow + expiry + EOD ───────────────────────────────────

    private async Task RunSlowCycleAsync(CancellationToken ct)
    {
        _logger.LogInformation("[PnLTracker] Slow cycle start.");

        using var scope = _scopeFactory.CreateScope();
        await ShadowExitSimulatorJob.RunAsync(scope, _logger, ct);
        await PaperTradeExpiryCloserJob.RunAsync(scope, _logger, ct);

        // EOD summary
        var istNow    = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Ist);
        var todayDate = DateOnly.FromDateTime(istNow);
        var timeNow   = TimeOnly.FromDateTime(istNow);

        if (timeNow >= EodSummaryTime && _lastEodDate != todayDate)
        {
            try
            {
                using var eodScope    = _scopeFactory.CreateScope();
                var analyticsRepo     = eodScope.ServiceProvider.GetRequiredService<ITradeAnalyticsRepository>();
                var userRepo          = eodScope.ServiceProvider.GetRequiredService<IUserProfileRepository>();
                var telegram          = eodScope.ServiceProvider.GetRequiredService<ITelegramNotificationService>();
                var encryption        = eodScope.ServiceProvider.GetRequiredService<IEncryptionService>();
                var marketData        = eodScope.ServiceProvider.GetRequiredService<IMarketDataProvider>();

                await SendEodSummaryAsync(
                    todayDate, _lastAllOpenPositions,
                    analyticsRepo, userRepo, telegram, encryption, marketData, ct);

                _lastEodDate = todayDate;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PnLTracker] EOD summary failed");
            }
        }

        _logger.LogInformation("[PnLTracker] Slow cycle complete.");
    }

    // ── EOD summary ───────────────────────────────────────────────────────────

    private async Task SendEodSummaryAsync(
        DateOnly date,
        List<Position> openPositions,
        ITradeAnalyticsRepository analyticsRepo,
        IUserProfileRepository userRepo,
        ITelegramNotificationService telegram,
        IEncryptionService encryption,
        IMarketDataProvider marketData,
        CancellationToken ct)
    {
        var closedToday = await analyticsRepo.GetClosedOnDateAsync(date, ct);

        decimal spot = 0;
        try
        {
            var q = await marketData.GetQuoteAsync("NIFTY 50", "NSE", ct);
            spot = q?.LastPrice ?? 0;
        }
        catch { }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("📊 *End of Day Summary*");
        sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━");
        sb.AppendLine($"📍 NIFTY: {spot:F0}");
        sb.AppendLine($"🗓 {date:dd MMM yyyy}  🕐 3:00 PM IST");
        sb.AppendLine();

        // ── Closed trades ──────────────────────────────────────────────────
        sb.AppendLine("*── Closed Trades ──*");
        if (!closedToday.Any())
        {
            sb.AppendLine("No trades closed today\\.");
        }
        else
        {
            foreach (var row in closedToday)
            {
                var pnlIcon    = row.NetPnL >= 0 ? "🟢" : "🔴";
                var quantity   = row.LotSize * 65;
                var entryCredit = row.PremiumCollected * quantity;
                sb.AppendLine($"*{row.StrategyName}*");
                sb.AppendLine($"  Strike: {row.ShortStrike:F0}/{row.LongStrike:F0}  Width: {row.SpreadWidth:F0}pts");
                sb.AppendLine($"  Entry credit: ₹{entryCredit:F0}");
                sb.AppendLine($"  Exit: {row.ExitReason}  Held: {row.HoldingMinutes}min");
                sb.AppendLine($"  {pnlIcon} Net P&L: ₹{row.NetPnL:F0}");
                sb.AppendLine();
            }

            var total     = closedToday.Sum(x => x.NetPnL ?? 0);
            var totalIcon = total >= 0 ? "🟢" : "🔴";
            sb.AppendLine($"{totalIcon} *Total Net P&L: ₹{total:F0}*");
        }

        // ── Open positions ─────────────────────────────────────────────────
        sb.AppendLine();
        sb.AppendLine("*── Open Positions ──*");
        if (!openPositions.Any())
        {
            sb.AppendLine("No open positions\\.");
        }
        else
        {
            var groups = openPositions.GroupBy(p => p.SignalGroupId);
            foreach (var group in groups)
            {
                var legs     = group.ToList();
                var shortLeg = legs.FirstOrDefault(p => p.Side == OrderSide.Sell);
                var longLeg  = legs.FirstOrDefault(p => p.Side == OrderSide.Buy);
                var mtm      = legs.Sum(p => p.UnrealizedPnL);
                var mtmIcon  = mtm >= 0 ? "🟢" : "🔴";

                var entryCredit = shortLeg is not null && longLeg is not null
                    ? (shortLeg.EntryPrice - longLeg.EntryPrice) * shortLeg.Quantity
                    : 0;

                
                var todayIst = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Ist));
                var daysToExpiry = shortLeg?.ExpiryDate.HasValue == true
                    ? shortLeg.ExpiryDate.Value.DayNumber - todayIst.DayNumber
                    : 0;

                
                var currentSpread = shortLeg is not null && longLeg is not null
                    ? Math.Abs(shortLeg.CurrentPrice - longLeg.CurrentPrice)
                    : 0;

                var symbol     = shortLeg?.TradingSymbol ?? legs.First().TradingSymbol;
                var underlying = symbol.StartsWith("FINNIFTY", StringComparison.OrdinalIgnoreCase)
                    ? "FINNIFTY" : "NIFTY";

                sb.AppendLine($"*{underlying} — {legs.Count} legs*");
                if (shortLeg is not null)
                    sb.AppendLine($"  📉 Short: {shortLeg.TradingSymbol} @₹{shortLeg.CurrentPrice:F2}");
                if (longLeg is not null)
                    sb.AppendLine($"  📈 Long:  {longLeg.TradingSymbol} @₹{longLeg.CurrentPrice:F2}");
                
                sb.AppendLine($" 💰 Entry credit:   ₹{entryCredit:F0}");
                sb.AppendLine($" 📊 Current spread: ₹{currentSpread:F2}");
                sb.AppendLine($" 🎯 Target: ₹{entryCredit * 0.5m:F0}");
                sb.AppendLine($" 🛑 SL at:  ₹{-entryCredit * 2m:F0}");
                sb.AppendLine($" ⏰ Days to expiry: {daysToExpiry}");
                //sb.AppendLine($" 📋 Mode: {execution.Mode}");
                sb.AppendLine($"  {mtmIcon} MTM: ₹{mtm:F0}");
                sb.AppendLine();
            }
        }

        // Send to all active users
        var users = await userRepo.GetAllActiveAsync(ct);
        foreach (var user in users)
        {
            try
            {
                var botToken = encryption.Decrypt(user.EncryptedTelegramBotToken);
                await telegram.SendMessageAsync(botToken, user.TelegramChatId, sb.ToString(), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PnLTracker] EOD send failed for user {UserId}", user.Id);
            }
        }

        _logger.LogInformation(
            "[PnLTracker] EOD summary sent | Date={D} ClosedTrades={C} OpenPositions={O} TotalPnL=₹{P:F0}",
            date, closedToday.Count, openPositions.Count, closedToday.Sum(x => x.NetPnL ?? 0));
    }

    private static bool IsMarketHours(TimeOnly time)
        => time >= MarketOpen && time <= MarketClose;

    private static bool IsWeekday(DateTime dt)
        => dt.DayOfWeek != DayOfWeek.Saturday && dt.DayOfWeek != DayOfWeek.Sunday;
}