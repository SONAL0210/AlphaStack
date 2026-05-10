using AlphaStack.Domain.Common;
using AlphaStack.Domain.Enums;

namespace AlphaStack.Domain.Entities;

/// <summary>
/// Per-user, per-mode execution of a StrategyDefinition.
/// Paper and Live are separate instances with isolated P&L.
/// This is what the background service runs against.
/// </summary>
public class StrategyExecution : BaseEntity
{
    public Guid UserProfileId { get; private set; }
    public Guid StrategyDefinitionId { get; private set; }
    public ExecutionMode Mode { get; private set; }

    public bool IsRunning { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public DateTime? StoppedAt { get; private set; }

    // Capital allocated for this specific execution
    public decimal AllocatedCapital { get; private set; }

    // Cumulative P&L for this execution
    public decimal RealizedPnL { get; private set; }
    public decimal UnrealizedPnL { get; private set; }
    public decimal TotalPnL => RealizedPnL + UnrealizedPnL;

    public int TotalTradesCount { get; private set; }
    public int WinningTradesCount { get; private set; }
    public decimal WinRate => TotalTradesCount == 0
        ? 0
        : (decimal)WinningTradesCount / TotalTradesCount * 100;

    public UserProfile UserProfile { get; private set; } = default!;
    public StrategyDefinition StrategyDefinition { get; private set; } = default!;

    public IReadOnlyCollection<TradeOrder> Orders => _orders.AsReadOnly();
    private readonly List<TradeOrder> _orders = [];

    public IReadOnlyCollection<Position> Positions => _positions.AsReadOnly();
    private readonly List<Position> _positions = [];

    private StrategyExecution() { }

    public static StrategyExecution Create(
        Guid userProfileId,
        Guid strategyDefinitionId,
        ExecutionMode mode,
        decimal allocatedCapital)
    {
        return new StrategyExecution
        {
            UserProfileId = userProfileId,
            StrategyDefinitionId = strategyDefinitionId,
            Mode = mode,
            AllocatedCapital = allocatedCapital
        };
    }

    public void Start()
    {
        if (IsRunning)
            throw new InvalidOperationException("Execution is already running.");

        IsRunning = true;
        StartedAt = DateTime.UtcNow;
        StoppedAt = null;
        MarkUpdated();
    }

    public void Stop()
    {
        IsRunning = false;
        StoppedAt = DateTime.UtcNow;
        MarkUpdated();
    }

    public void RecordFilledTrade(decimal realizedPnl)
    {
        TotalTradesCount++;
        RealizedPnL += realizedPnl;

        if (realizedPnl > 0)
            WinningTradesCount++;

        MarkUpdated();
    }

    public void UpdateUnrealizedPnL(decimal unrealizedPnl)
    {
        UnrealizedPnL = unrealizedPnl;
        MarkUpdated();
    }
}
