using AlphaStack.Domain.Common;
using AlphaStack.Domain.Enums;
using AlphaStack.Domain.Exceptions;

namespace AlphaStack.Domain.Entities;

/// <summary>
/// Aggregate root that ties together entry orders → open position → exit orders
/// into a single traceable unit with enforced state transitions.
/// </summary>
public class Trade : BaseEntity
{
    // ── Identity ──────────────────────────────────────────────────────────────

    public Guid StrategyExecutionId { get; private set; }

    /// <summary>TradingSymbol of the short leg (primary leg for spreads).</summary>
    public string Symbol { get; private set; } = default!;

    public TradeDirection Direction { get; private set; }
    public TradeStatus Status { get; private set; } = TradeStatus.Created;

    // ── Pricing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Net credit received at entry (for spreads: sell LTP - buy LTP).
    /// Zero until entry is filled.
    /// </summary>
    public decimal EntryPrice { get; private set; }

    public decimal? ExitPrice { get; private set; }

    /// <summary>Use decimal (not int) to be future-proof for fractional sizing.</summary>
    public decimal Quantity { get; private set; }

    /// <summary>Computed and stored on Close() — do not recalculate elsewhere.</summary>
    public decimal? RealizedPnL { get; private set; }

    // ── Timestamps ────────────────────────────────────────────────────────────

    /// <summary>When the Trade record was created (before fill).</summary>
    public new DateTime CreatedAt { get; private set; }

    /// <summary>When the entry order was actually filled.</summary>
    public DateTime? EntryTime { get; private set; }

    public DateTime? ExitTime { get; private set; }

    // ── Signal / Order linkage ────────────────────────────────────────────────

    public Guid EntrySignalGroupId { get; private set; }
    public Guid? ExitSignalGroupId { get; private set; }

    /// <summary>Idempotency key — generated before broker call, persisted first.</summary>
    public string? EntryClientOrderId { get; private set; }
    public string? ExitClientOrderId { get; private set; }

    // ── Navigation ────────────────────────────────────────────────────────────

    public StrategyExecution StrategyExecution { get; private set; } = default!;

    // ── Valid state transitions ───────────────────────────────────────────────

    private static readonly Dictionary<TradeStatus, HashSet<TradeStatus>> _validTransitions = new()
    {
        [TradeStatus.Created]      = [TradeStatus.EntryPending],
        [TradeStatus.EntryPending] = [TradeStatus.Entered,     TradeStatus.Failed],
        [TradeStatus.Entered]      = [TradeStatus.ExitPending],
        [TradeStatus.ExitPending]  = [TradeStatus.Closed,      TradeStatus.Failed],
        [TradeStatus.Closed]       = [],
        [TradeStatus.Failed]       = [],
    };

    // ── Constructor ───────────────────────────────────────────────────────────

    private Trade() { }

    // ── Factory ───────────────────────────────────────────────────────────────

    public static Trade Create(
        Guid strategyExecutionId,
        string symbol,
        TradeDirection direction,
        decimal quantity,
        Guid entrySignalGroupId,
        string entryClientOrderId)
    {
        return new Trade
        {
            StrategyExecutionId  = strategyExecutionId,
            Symbol               = symbol,
            Direction            = direction,
            Quantity             = quantity,
            EntrySignalGroupId   = entrySignalGroupId,
            EntryClientOrderId   = entryClientOrderId,
            Status               = TradeStatus.Created,
            CreatedAt            = DateTime.UtcNow
        };
    }

    // ── State transition methods ──────────────────────────────────────────────

    public void MarkEntryPending()
    {
        Transition(TradeStatus.EntryPending);
    }

    public void MarkEntered(decimal entryPrice, DateTime entryTime)
    {
        Transition(TradeStatus.Entered);
        EntryPrice = entryPrice;
        EntryTime  = entryTime;
        MarkUpdated();
    }

    public void MarkExitPending(Guid exitSignalGroupId, string exitClientOrderId)
    {
        Transition(TradeStatus.ExitPending);
        ExitSignalGroupId   = exitSignalGroupId;
        ExitClientOrderId   = exitClientOrderId;
        MarkUpdated();
    }

    public void Close(decimal exitPrice, DateTime exitTime)
    {
        Transition(TradeStatus.Closed);
        ExitPrice    = exitPrice;
        ExitTime     = exitTime;
        RealizedPnL  = ComputePnL(exitPrice);
        MarkUpdated();
    }

    public void MarkFailed()
    {
        Transition(TradeStatus.Failed);
    }

    /// <summary>
    /// Closes a trade that reached expiry without a formal exit signal.
    /// Bypasses the normal ExitPending → Closed state machine because no exit
    /// orders are placed at expiry — options simply expire worthless.
    /// Only valid from Entered status.
    /// </summary>
    public void ForceCloseAtExpiry(decimal exitPrice, DateTime exitTime)
    {
        if (Status != TradeStatus.Entered)
            throw new InvalidOperationException(
                $"ForceCloseAtExpiry only valid from Entered status. Current: {Status}");

        Status      = TradeStatus.Closed;
        ExitPrice   = exitPrice;
        ExitTime    = exitTime;
        RealizedPnL = ComputePnL(exitPrice);
        MarkUpdated();
    }

    // ── Guard ─────────────────────────────────────────────────────────────────

    private void Transition(TradeStatus next)
    {
        if (!_validTransitions[Status].Contains(next))
            throw new InvalidTradeTransitionException(Status, next);

        Status = next;
        MarkUpdated();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private decimal ComputePnL(decimal exitPrice) => Direction == TradeDirection.Short
        ? (EntryPrice - exitPrice) * Quantity   // Short: profit when price falls
        : (exitPrice - EntryPrice) * Quantity;  // Long:  profit when price rises
}
