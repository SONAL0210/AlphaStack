using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Application.Features.Trading;
using AlphaStack.Infrastructure.ExternalServices.Fyers;

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

    private static readonly TimeZoneInfo Ist =
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    // Evaluate entries shortly after open — give market 5 min to settle
    private static readonly TimeOnly EntryEvalTime = new(23 ,14);

    public StrategyRunnerService(
        IServiceScopeFactory scopeFactory,
        FyersTokenService tokenService,
        ILogger<StrategyRunnerService> logger)
    {
        _scopeFactory = scopeFactory;
        _tokenService = tokenService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[StrategyRunner] Service started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = GetDelayUntilNextEvaluation();
            _logger.LogInformation(
                "[StrategyRunner] Next entry evaluation in {Delay}.", delay);

            using var wakeCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var delayTask = Task.Delay(delay, wakeCts.Token);
            var refreshTask = _tokenService.WaitForTokenRefreshAsync(wakeCts.Token);
            var completed = await Task.WhenAny(delayTask, refreshTask);
            await wakeCts.CancelAsync();

            if (stoppingToken.IsCancellationRequested) break;

            if (completed == refreshTask)
                _logger.LogInformation("[StrategyRunner] Fyers token refreshed — running entry evaluation now.");

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

                var engine = engineFactory.Resolve(strategyDef.StrategyType);
                var signal = await engine.EvaluateAsync(execution, ct);

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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[StrategyRunner] Error evaluating execution {ExecId}", execution.Id);
            }
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
