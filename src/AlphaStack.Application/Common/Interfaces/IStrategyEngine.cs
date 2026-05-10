using AlphaStack.Domain.Entities;

namespace AlphaStack.Application.Common.Interfaces;

/// <summary>
/// Contract that every strategy engine must implement.
/// The background service calls EvaluateAsync on a schedule.
/// Returns a signal if entry/exit conditions are met, null otherwise.
/// </summary>
public interface IStrategyEngine
{
    string StrategyType { get; }

    /// <summary>
    /// Evaluate market conditions for this execution.
    /// Returns a signal if conditions are met, null if no action required.
    /// </summary>
    Task<StrategySignal?> EvaluateAsync(
        StrategyExecution execution,
        CancellationToken ct = default);

    /// <summary>
    /// Evaluate exit conditions for open positions.
    /// Called separately from entry evaluation on the same schedule.
    /// </summary>
    Task<StrategySignal?> EvaluateExitAsync(
        StrategyExecution execution,
        CancellationToken ct = default);
}

/// <summary>
/// Resolves the correct IStrategyEngine implementation by strategy type string.
/// Registered in DI and injected into the background service orchestrator.
/// </summary>
public interface IStrategyEngineFactory
{
    IStrategyEngine Resolve(string strategyType);
}
