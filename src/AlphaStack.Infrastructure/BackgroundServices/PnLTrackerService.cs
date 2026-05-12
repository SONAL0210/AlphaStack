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
/// Runs every 5 minutes during NSE market hours (9:15 – 15:30 IST).
/// For each running execution:
///   1. Refreshes current prices on all open positions
///   2. Evaluates exit conditions via the strategy engine
///   3. Auto-exits if triggered (no Telegram approval required)
/// </summary>
public class PnLTrackerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FyersTokenService _tokenService;
    private readonly ILogger<PnLTrackerService> _logger;

    private static readonly TimeZoneInfo Ist =
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    private static readonly TimeOnly MarketOpen = new(9, 15);
    private static readonly TimeOnly MarketClose = new(15, 30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private static readonly TimeOnly EodSummaryTime = new(15, 25);
    private bool _eodSummarySentToday = false;
    private DateOnly _lastEodDate = DateOnly.MinValue;

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
                var istNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Ist);
                var timeNow = TimeOnly.FromDateTime(istNow);

                if (IsMarketHours(timeNow) && IsWeekday(istNow))
                {
                    await RunTrackerCycleAsync(stoppingToken);
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
            var delayTask = Task.Delay(PollInterval, wakeCts.Token);
            var refreshTask = _tokenService.WaitForTokenRefreshAsync(wakeCts.Token);
            var completed = await Task.WhenAny(delayTask, refreshTask);
            await wakeCts.CancelAsync();

            if (stoppingToken.IsCancellationRequested) break;

            if (completed == refreshTask)
                _logger.LogInformation("[PnLTracker] Fyers token refreshed — running next cycle now.");
        }

        _logger.LogInformation("[PnLTracker] Service stopped.");
    }

    private async Task RunTrackerCycleAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var executionRepo    = scope.ServiceProvider.GetRequiredService<IStrategyExecutionRepository>();
        var positionRepo     = scope.ServiceProvider.GetRequiredService<IPositionRepository>();
        var strategyDefRepo  = scope.ServiceProvider.GetRequiredService<IStrategyDefinitionRepository>();
        var engineFactory    = scope.ServiceProvider.GetRequiredService<IStrategyEngineFactory>();
        var signalProcessor  = scope.ServiceProvider.GetRequiredService<SignalProcessor>();
        var analyticsRepo    = scope.ServiceProvider.GetRequiredService<ITradeAnalyticsRepository>();
        var userRepo         = scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();
        var telegram         = scope.ServiceProvider.GetRequiredService<ITelegramNotificationService>();
        var encryption       = scope.ServiceProvider.GetRequiredService<IEncryptionService>();
        var marketData       = scope.ServiceProvider.GetRequiredService<IMarketDataProvider>();
        var uow              = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var istNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Ist);
        var runningExecutions = await executionRepo.GetRunningExecutionsAsync(ct);

        _logger.LogInformation(
            "[PnLTracker] Cycle start — {Count} running executions.", runningExecutions.Count);

        var todayDate = DateOnly.FromDateTime(istNow);
        var timeNow = TimeOnly.FromDateTime(istNow);
        var shouldSendEod = timeNow >= EodSummaryTime && _lastEodDate != todayDate;

        if (shouldSendEod)
            _lastEodDate = todayDate; 

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

                // ── Refresh live LTP for each open leg ────────────────────────────────────
                foreach (var pos in openPositions)
                {
                    try
                    {
                        var quote = await marketData.GetQuoteAsync(
                            pos.TradingSymbol,
                            pos.Exchange.ToString(),
                            ct);
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
                                    var spotSymbol = shortLegPos.TradingSymbol.StartsWith("BANKNIFTY", StringComparison.OrdinalIgnoreCase)
                                        ? "NIFTY BANK"
                                        : "NIFTY 50";
                                    var spotQ = await marketData.GetQuoteAsync(spotSymbol, "NSE", ct);
                                    if (spotQ is not null && spotQ.LastPrice <= shortLegPos.StrikePrice.Value)
                                    {
                                        analytics.MarkShortStrikeTouched();
                                        _logger.LogWarning(
                                            "[PnLTracker] Short strike TOUCHED | Spot={Spot:F0} <= Strike={Strike:F0} | GroupId={G}",
                                            spotQ.LastPrice, shortLegPos.StrikePrice.Value, signalGroupId);
                                    }
                                }
                                catch (Exception spotEx)
                                {
                                    _logger.LogDebug(spotEx, "[PnLTracker] Strike breach spot fetch failed for {G}", signalGroupId);
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
                        // Analytics failure must never block P&L tracking
                        _logger.LogWarning(ex, "[PnLTracker] MTM analytics update failed for group {G}", signalGroupId);
                    }
                }

                // shouldSendEod is set once before the loop — fires for the FIRST execution
                // with open positions only, then the flag is consumed.
                if (shouldSendEod && openPositions.Any())
                {
                    try
                    {
                        await SendEodSummaryAsync(
                            execution, openPositions.ToList(),
                            unrealizedPnL, userRepo, telegram, encryption, marketData, ct);
                        shouldSendEod = false; // consume — only one summary per day
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[PnLTracker] EOD summary failed for execution {ExecId}", execution.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PnLTracker] Error processing execution {ExecId}", execution.Id);
            }
        }

        await ShadowExitSimulatorJob.RunAsync(scope, _logger, ct);

        await uow.SaveChangesAsync(ct);
        _logger.LogInformation("[PnLTracker] Cycle complete.");
    }

    private async Task SendEodSummaryAsync(
    StrategyExecution execution,
    List<Position> openPositions,
    decimal unrealizedPnL,
    IUserProfileRepository userRepo,
    ITelegramNotificationService telegram,
    IEncryptionService encryption,
    IMarketDataProvider marketData,
    CancellationToken ct)
    {
        var user = await userRepo.GetByIdAsync(execution.UserProfileId, ct);
        if (user is null) return;

        var botToken = encryption.Decrypt(user.EncryptedTelegramBotToken);

        // Fetch current Nifty spot
        decimal spot = 0;
        try
        {
            var spotSymbol = openPositions.Any(p => p.TradingSymbol.StartsWith("BANKNIFTY",
                        StringComparison.OrdinalIgnoreCase))
                        ? "NIFTY BANK" : "NIFTY 50";
            var q = await marketData.GetQuoteAsync(spotSymbol, "NSE", ct);
            spot = q?.LastPrice ?? 0;
        }
        catch { }

        // Build position summary
        var shortLeg = openPositions.FirstOrDefault(p => p.Side == OrderSide.Sell);
        var longLeg  = openPositions.FirstOrDefault(p => p.Side == OrderSide.Buy);

        var entryCredit = shortLeg is not null && longLeg is not null
            ? (shortLeg.EntryPrice - longLeg.EntryPrice) * shortLeg.Quantity
            : 0;

        var pnlIcon = unrealizedPnL >= 0 ? "🟢" : "🔴";
        var daysToExpiry = shortLeg?.ExpiryDate.HasValue == true
            ? (shortLeg.ExpiryDate.Value.ToDateTime(TimeOnly.MinValue) - DateTime.Today).Days
            : 0;

        var currentSpread = shortLeg is not null && longLeg is not null
            ? shortLeg.CurrentPrice - longLeg.CurrentPrice
            : 0;

        var underlying = openPositions.Any(p => p.TradingSymbol.StartsWith("BANKNIFTY",
            StringComparison.OrdinalIgnoreCase)) ? "BANKNIFTY" : "NIFTY";

        var msg = $"""
            📊 *End of Day Summary*
            ━━━━━━━━━━━━━━━━━━━━━━

            📍 {underlying}: {spot:F0}
            🕐 Time: 3:25 PM IST

            📉 Short: {shortLeg?.TradingSymbol ?? "-"} @₹{shortLeg?.CurrentPrice:F2}
            📈 Long:  {longLeg?.TradingSymbol ?? "-"} @₹{longLeg?.CurrentPrice:F2}

            💰 Entry credit:   ₹{entryCredit:F0}
            📊 Current spread: ₹{currentSpread:F2}
            {pnlIcon} Unrealized P&L: ₹{unrealizedPnL:F0}

            🎯 Target: ₹{entryCredit * 0.5m:F0}
            🛑 SL at:  ₹{-entryCredit * 2m:F0}

            ⏰ Days to expiry: {daysToExpiry}
            📋 Mode: {execution.Mode}
            """;

        await telegram.SendMessageAsync(botToken, user.TelegramChatId, msg, ct);

        _logger.LogInformation(
            "[PnLTracker] EOD summary sent | Spot={S} UnrealizedPnL=₹{P:F0}",
            spot, unrealizedPnL);
    }

    private static bool IsMarketHours(TimeOnly time)
        => time >= MarketOpen && time <= MarketClose;

    private static bool IsWeekday(DateTime dt)
        => dt.DayOfWeek != DayOfWeek.Saturday && dt.DayOfWeek != DayOfWeek.Sunday;
}
