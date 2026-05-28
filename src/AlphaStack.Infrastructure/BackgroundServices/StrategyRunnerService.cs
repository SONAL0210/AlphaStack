using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Application.Features.Trading;
using AlphaStack.Infrastructure.ExternalServices.Fyers;
using AlphaStack.Infrastructure.Strategies;

namespace AlphaStack.Infrastructure.BackgroundServices;

/// <summary>
/// Evaluates entry signals for all running executions.
/// Runs once per day at market open (9:20 IST) — entry signals are daily.
/// Strategy engines decide internally if today is a valid entry day.
/// </summary>
public class StrategyRunnerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FyersTokenService _tokenService;
    private readonly ILogger<StrategyRunnerService> _logger;
    private readonly IMarketDataProvider _marketData;

    private DateTime _lastEvaluationTime = DateTime.MinValue;
    private DateOnly _lastEvaluationDate = DateOnly.MinValue; 

    private static readonly TimeZoneInfo Ist =
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    // Evaluate entries shortly after open — give market 5 min to settle
    private static readonly TimeOnly EntryEvalTime = new(09, 20);

    public StrategyRunnerService(
        IServiceScopeFactory scopeFactory,
        FyersTokenService tokenService,
        IMarketDataProvider marketData,
        ILogger<StrategyRunnerService> logger)
    {
        _scopeFactory = scopeFactory;
        _tokenService = tokenService;
        _marketData = marketData;
        _logger = logger;
    }
    
    private async Task<bool> IsMarketHolidayAsync(CancellationToken ct)
    {
        var istNow = DateTime.UtcNow.AddHours(5).AddMinutes(30);
        
        // Only check after 9:15 — before that candle won't exist even on trading days
        if (TimeOnly.FromDateTime(istNow) < new TimeOnly(9, 15))
            return false;

        var from = istNow.Date;
        var to   = from.AddHours(23).AddMinutes(59);

        var candles = await _marketData.GetHistoricalDataAsync(
            256265, "1D", from, to, ct);

        return candles.Count == 0;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[StrategyRunner] Service started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = GetDelayUntilNextEvaluation();
            _logger.LogInformation("[StrategyRunner] Next entry evaluation in {Delay}.", delay);

            using var wakeCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var delayTask = Task.Delay(delay, wakeCts.Token);
            var refreshTask = _tokenService.WaitForTokenRefreshAsync(wakeCts.Token);
            var completed = await Task.WhenAny(delayTask, refreshTask);
            await wakeCts.CancelAsync();

            if (stoppingToken.IsCancellationRequested) break;

            if (completed == refreshTask)
            {
                _logger.LogInformation("[StrategyRunner] Fyers token refreshed — waiting for market open.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

                var istNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Ist);
                var todayIst = DateOnly.FromDateTime(istNow);
                var timeNow = TimeOnly.FromDateTime(istNow);

                if (_lastEvaluationDate == todayIst)
                {
                    _logger.LogInformation("[StrategyRunner] Skipping — already evaluated today ({Date})", todayIst);
                    continue;
                }

                if (timeNow < EntryEvalTime)
                {
                    _logger.LogInformation("[StrategyRunner] Token refreshed before market open — skipping early evaluation. Will run at {Time}.", EntryEvalTime);
                    continue;
                }

                _logger.LogInformation("[StrategyRunner] Token refreshed after market open — running evaluation now.");
            }

            // ✅ Holiday check here — inside loop, on both scheduled and token-refresh paths
            if (await IsMarketHolidayAsync(stoppingToken))
            {
                _logger.LogInformation("[StrategyRunner] Market holiday detected — skipping evaluation.");
                continue; // ✅ continue not return — loop runs again tomorrow
            }

            var istFinal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Ist);
            _lastEvaluationDate = DateOnly.FromDateTime(istFinal);
            _lastEvaluationTime = DateTime.UtcNow;

            await RunEntryEvaluationAsync(stoppingToken);
        }

        _logger.LogInformation("[StrategyRunner] Service stopped.");
    }

    private async Task RunEntryEvaluationAsync(CancellationToken ct)
    {
        _logger.LogInformation("[StrategyRunner] Entry evaluation starting.");

        using var scope = _scopeFactory.CreateScope();
        var executionRepo = scope.ServiceProvider.GetRequiredService<IStrategyExecutionRepository>();
        var strategyDefRepo = scope.ServiceProvider.GetRequiredService<IStrategyDefinitionRepository>();
        var engineFactory = scope.ServiceProvider.GetRequiredService<IStrategyEngineFactory>();
        var signalProcessor = scope.ServiceProvider.GetRequiredService<SignalProcessor>();

        var runningExecutions = await executionRepo.GetRunningExecutionsAsync(ct);

        foreach (var execution in runningExecutions)
        {
            try
            {
                var strategyDef = await strategyDefRepo.GetByIdAsync(execution.StrategyDefinitionId, ct);
                if (strategyDef is null) continue;

                // StrategyRunnerService.cs - inside RunEntryEvaluationAsync foreach loop:
                var engine = engineFactory.Resolve(strategyDef.StrategyType);
                var signal = await engine.EvaluateAsync(execution, ct);

                // Always grab shadow context — engine sets it even on skips (after gate 5)
                var shadowContext = (engine as BaseSpreadEngine)?.LastEvaluatedShadowContext;

                if (signal is not null)
                {
                    _logger.LogInformation(
                        "[StrategyRunner] Signal generated for execution {ExecId} ({Strategy})",
                        execution.Id, strategyDef.StrategyType);
                    await signalProcessor.ProcessAsync(signal, ct);
                }
                else
                {
                    _logger.LogInformation(
                        "[StrategyRunner] No signal for execution {ExecId} ({Strategy})",
                        execution.Id, strategyDef.StrategyType);
                }

                // Fire shadow log regardless of signal outcome — but only if we got market context
                // (context is null if engine skipped before fetching indicators, e.g. wrong day)
                if (shadowContext is not null)
                {
                    var capturedStrategyType  = strategyDef.StrategyType;
                    var capturedSignalGroupId = signal?.SignalGroupId;
                    var capturedContext       = shadowContext;

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var shadowScope  = _scopeFactory.CreateScope();
                            var shadowLogger       = shadowScope.ServiceProvider
                                .GetRequiredService<ShadowTradeLoggerService>();

                            var istNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Ist);
                            var entryVariation = istNow.DayOfWeek switch
                            {
                                DayOfWeek.Monday    => "MondayEntry",
                                DayOfWeek.Tuesday   => "TuesdayEntry",
                                DayOfWeek.Wednesday => "WednesdayEntry",
                                DayOfWeek.Thursday  => "ThursdayEntry",
                                DayOfWeek.Friday    => "FridayEntry",
                                _                   => "UnknownEntry"
                            };

                            var isFinnifty   = capturedStrategyType.Contains("Finnifty");
                            var isIronCondor = capturedStrategyType.Contains("IronCondor");

                            if (isIronCondor)
                            {
                                // Log both wings as separate strategy name variants
                                // Both share the same realSignalGroupId for linkability
                                await shadowLogger.LogVariantsAsync(
                                    context:           capturedContext,
                                    realSignalGroupId: capturedSignalGroupId,
                                    strategyName:      $"{capturedStrategyType}_Put",
                                    entryVariation:    entryVariation,
                                    strikeInterval:    isFinnifty ? 50 : 50,
                                    quantity:          isFinnifty ? 40 : 65,
                                    realAdrMultiplier: 1.5m,
                                    realSpreadWidth:   200,
                                    ct:                CancellationToken.None);

                                await shadowLogger.LogVariantsAsync(
                                    context:           capturedContext,
                                    realSignalGroupId: capturedSignalGroupId,
                                    strategyName:      $"{capturedStrategyType}_Call",
                                    entryVariation:    entryVariation,
                                    strikeInterval:    isFinnifty ? 50 : 50,
                                    quantity:          isFinnifty ? 40 : 65,
                                    realAdrMultiplier: 1.5m,
                                    realSpreadWidth:   200,
                                    ct:                CancellationToken.None);
                            }
                            else
                            {
                                await shadowLogger.LogVariantsAsync(
                                    context:           capturedContext,
                                    realSignalGroupId: capturedSignalGroupId,
                                    strategyName:      capturedStrategyType,
                                    entryVariation:    entryVariation,
                                    strikeInterval:    isFinnifty ? 50 : 50,
                                    quantity:          isFinnifty ? 40 : 65,
                                    realAdrMultiplier: 1.5m,
                                    realSpreadWidth:   200,
                                    ct:                CancellationToken.None);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[StrategyRunner] Shadow log failed for {Strategy}",
                                capturedStrategyType);
                        }
                    }, CancellationToken.None);
                }
                
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[StrategyRunner] Error evaluating execution {ExecId}", execution.Id);
            }
            await Task.Delay(800, ct);
        }

        _logger.LogInformation("[StrategyRunner] Entry evaluation complete.");
    }

    /// <summary>
    /// Calculate delay until next 9:20 IST on a weekday.
    /// If it's already past 9:20 today, schedule for tomorrow (or Monday if Friday).
    /// </summary>
    private static TimeSpan GetDelayUntilNextEvaluation()
    {
        var nowIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Ist);
        var todayEval = nowIst.Date.Add(EntryEvalTime.ToTimeSpan());


        DateTime nextEval;

        if (nowIst < todayEval && nowIst.DayOfWeek != DayOfWeek.Saturday
                               && nowIst.DayOfWeek != DayOfWeek.Sunday)
        {
            nextEval = todayEval;
        }
        else
        {
            // Move to next weekday
            var candidate = nowIst.Date.AddDays(1);
            while (candidate.DayOfWeek == DayOfWeek.Saturday ||
                   candidate.DayOfWeek == DayOfWeek.Sunday)
                candidate = candidate.AddDays(1);

            nextEval = candidate.Add(EntryEvalTime.ToTimeSpan());
        }

        var nextEvalUtc = TimeZoneInfo.ConvertTimeToUtc(nextEval, Ist);
        return nextEvalUtc - DateTime.UtcNow;
    }
}
