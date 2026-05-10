using AlphaStack.Domain.Common;
using AlphaStack.Domain.Enums;

namespace AlphaStack.Domain.Entities;

/// <summary>
/// Shared, versioned strategy template. Visible to all users.
/// Not tied to any user — users subscribe via StrategyExecution.
/// </summary>
public class StrategyDefinition : BaseEntity
{
    public string Name { get; private set; } = default!;
    public string Description { get; private set; } = default!;
    public StrategyLifecycleStage Stage { get; private set; }

    // Strategy metadata
    public string StrategyType { get; private set; } = default!;    // e.g. "BullPutSpread"
    public string MarketRegime { get; private set; } = default!;    // e.g. "LowVolatility_Bullish"
    public int Version { get; private set; } = 1;

    // Parameters stored as JSON — strategy-specific config
    public string ParametersJson { get; private set; } = "{}";

    public bool IsActive { get; private set; } = true;

    public IReadOnlyCollection<StrategyExecution> Executions => _executions.AsReadOnly();
    private readonly List<StrategyExecution> _executions = [];

    private StrategyDefinition() { }

    public static StrategyDefinition Create(
        string name,
        string description,
        string strategyType,
        string marketRegime,
        StrategyLifecycleStage initialStage = StrategyLifecycleStage.PaperTrading)
    {
        return new StrategyDefinition
        {
            Name = name,
            Description = description,
            StrategyType = strategyType,
            MarketRegime = marketRegime,
            Stage = initialStage
        };
    }

    public void AdvanceStage(StrategyLifecycleStage newStage)
    {
        // Enforce valid transitions
        var valid = (Stage, newStage) switch
        {
            (StrategyLifecycleStage.Hypothesis, StrategyLifecycleStage.Backtesting) => true,
            (StrategyLifecycleStage.Backtesting, StrategyLifecycleStage.PaperTrading) => true,
            (StrategyLifecycleStage.PaperTrading, StrategyLifecycleStage.Live) => true,
            (_, StrategyLifecycleStage.Deprecated) => true,
            _ => false
        };

        if (!valid)
            throw new InvalidOperationException(
                $"Invalid stage transition: {Stage} → {newStage}");

        Stage = newStage;
        Version++;
        MarkUpdated();
    }

    public void UpdateParameters(string parametersJson)
    {
        ParametersJson = parametersJson;
        MarkUpdated();
    }
}
