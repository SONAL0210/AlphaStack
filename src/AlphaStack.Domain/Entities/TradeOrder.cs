using AlphaStack.Domain.Common;
using AlphaStack.Domain.Enums;

namespace AlphaStack.Domain.Entities;

/// <summary>
/// Single-leg order. Multi-leg strategies (like Bull Put Spread) generate
/// multiple TradeOrders grouped by a common SignalGroupId.
/// </summary>
public class TradeOrder : BaseEntity
{
    public Guid StrategyExecutionId { get; private set; }
    public ExecutionMode Mode { get; private set; }

    public Guid SignalGroupId { get; private set; }

    // Instrument details
    public string TradingSymbol { get; private set; } = default!;
    public string Exchange { get; private set; } = default!;
    public int InstrumentToken { get; private set; }
    public InstrumentType InstrumentType { get; private set; }

    // Options details
    public OptionType? OptionType { get; private set; }
    public decimal? StrikePrice { get; private set; }
    public DateOnly? ExpiryDate { get; private set; }

    // Order details
    public OrderSide Side { get; private set; }
    public OrderType OrderType { get; private set; }
    public int Quantity { get; private set; }
    public decimal? LimitPrice { get; private set; }
    public decimal? TriggerPrice { get; private set; }

    // Execution state
    public OrderStatus Status { get; private set; } = OrderStatus.Pending;
    public decimal? FilledPrice { get; private set; }
    public int FilledQuantity { get; private set; }
    public DateTime? FilledAt { get; private set; }

    // Idempotency key — nullable, assigned via AssignClientOrderId() AFTER DB save.
    // DO NOT set inside Create() — a new GUID per retry defeats the purpose.
    public string? ClientOrderId { get; private set; }

    // Broker order ID (null for paper orders)
    public string? BrokerOrderId { get; private set; }

    // Telegram approval
    public string? TelegramMessageId { get; private set; }
    public DateTime? ApprovalRequestedAt { get; private set; }
    public DateTime? ApprovedAt { get; private set; }

    public StrategyExecution StrategyExecution { get; private set; } = default!;

    private TradeOrder() { }

    public static TradeOrder Create(
        Guid strategyExecutionId,
        ExecutionMode mode,
        Guid signalGroupId,
        string tradingSymbol,
        string exchange,
        int instrumentToken,
        InstrumentType instrumentType,
        OrderSide side,
        OrderType orderType,
        int quantity,
        decimal? limitPrice = null,
        decimal? triggerPrice = null,
        OptionType? optionType = null,
        decimal? strikePrice = null,
        DateOnly? expiryDate = null)
    {
        return new TradeOrder
        {
            StrategyExecutionId = strategyExecutionId,
            Mode                = mode,
            SignalGroupId       = signalGroupId,
            TradingSymbol       = tradingSymbol,
            Exchange            = exchange,
            InstrumentToken     = instrumentToken,
            InstrumentType      = instrumentType,
            Side                = side,
            OrderType           = orderType,
            Quantity            = quantity,
            LimitPrice          = limitPrice,
            TriggerPrice        = triggerPrice,
            OptionType          = optionType,
            StrikePrice         = strikePrice,
            ExpiryDate          = expiryDate
            // ClientOrderId intentionally not set here
        };
    }

    /// <summary>
    /// Assign idempotency key. Call AFTER AddAsync + SaveChangesAsync,
    /// BEFORE the broker/simulator call. Safe retries will find the same key.
    /// </summary>
    public void AssignClientOrderId(string clientOrderId)
    {
        if (ClientOrderId is not null)
            throw new InvalidOperationException(
                $"ClientOrderId already assigned on order {Id}. Cannot reassign.");

        ClientOrderId = clientOrderId;
        MarkUpdated();
    }

    public void MarkApprovalRequested(string telegramMessageId)
    {
        TelegramMessageId   = telegramMessageId;
        ApprovalRequestedAt = DateTime.UtcNow;
        MarkUpdated();
    }

    public void Approve()
    {
        if (Status != OrderStatus.Pending)
            throw new InvalidOperationException($"Cannot approve order in status {Status}.");

        Status     = OrderStatus.Approved;
        ApprovedAt = DateTime.UtcNow;
        MarkUpdated();
    }

    public void Reject()
    {
        if (Status != OrderStatus.Pending)
            throw new InvalidOperationException($"Cannot reject order in status {Status}.");

        Status = OrderStatus.Rejected;
        MarkUpdated();
    }

    public void MarkPlaced(string? brokerOrderId = null)
    {
        Status        = OrderStatus.Placed;
        BrokerOrderId = brokerOrderId;
        MarkUpdated();
    }

    public void MarkFilled(decimal filledPrice, int filledQuantity)
    {
        Status         = OrderStatus.Filled;
        FilledPrice    = filledPrice;
        FilledQuantity = filledQuantity;
        FilledAt       = DateTime.UtcNow;
        MarkUpdated();
    }

    public void MarkFailed()
    {
        Status = OrderStatus.Failed;
        MarkUpdated();
    }

    public decimal? GrossValue => FilledPrice.HasValue
        ? FilledPrice.Value * FilledQuantity
        : null;
}
