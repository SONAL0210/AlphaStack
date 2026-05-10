using AlphaStack.Domain.Common;
using AlphaStack.Domain.Enums;

namespace AlphaStack.Domain.Entities;

/// <summary>
/// Represents a strategy leg position (e.g. short put at 24000 strike).
/// A Bull Put Spread has 2 positions — one short put, one long put.
/// </summary>
public class Position : BaseEntity
{
    public Guid StrategyExecutionId { get; private set; }
    public ExecutionMode Mode { get; private set; }
    public Guid SignalGroupId { get; private set; }

    public string TradingSymbol { get; private set; } = default!;
    public string Exchange { get; private set; } = default!;
    public int InstrumentToken { get; private set; }

    // Options details
    public OptionType? OptionType { get; private set; }
    public decimal? StrikePrice { get; private set; }
    public DateOnly? ExpiryDate { get; private set; }

    public OrderSide Side { get; private set; }   // Buy = long leg, Sell = short leg
    public int Quantity { get; private set; }
    public decimal EntryPrice { get; private set; }
    public decimal? ExitPrice { get; private set; }
    public decimal CurrentPrice { get; private set; }

    public PositionStatus Status { get; private set; } = PositionStatus.Open;
    public DateTime OpenedAt { get; private set; }
    public DateTime? ClosedAt { get; private set; }

    // P&L
    public decimal UnrealizedPnL => Status == PositionStatus.Open
        ? CalculatePnL(CurrentPrice)
        : 0;

    public decimal RealizedPnL => Status == PositionStatus.Closed && ExitPrice.HasValue
        ? CalculatePnL(ExitPrice.Value)
        : 0;

    public StrategyExecution StrategyExecution { get; private set; } = default!;

    private Position() { }

    public static Position Open(
        Guid strategyExecutionId,
        ExecutionMode mode,
        Guid signalGroupId,
        string tradingSymbol,
        string exchange,
        int instrumentToken,
        OrderSide side,
        int quantity,
        decimal entryPrice,
        OptionType? optionType = null,
        decimal? strikePrice = null,
        DateOnly? expiryDate = null)
    {
        return new Position
        {
            StrategyExecutionId = strategyExecutionId,
            Mode = mode,
            SignalGroupId = signalGroupId,
            TradingSymbol = tradingSymbol,
            Exchange = exchange,
            InstrumentToken = instrumentToken,
            Side = side,
            Quantity = quantity,
            EntryPrice = entryPrice,
            CurrentPrice = entryPrice,
            OpenedAt = DateTime.UtcNow,
            OptionType = optionType,
            StrikePrice = strikePrice,
            ExpiryDate = expiryDate
        };
    }

    public void UpdateCurrentPrice(decimal currentPrice)
    {
        CurrentPrice = currentPrice;
        MarkUpdated();
    }

    public void Close(decimal exitPrice)
    {
        if (Status == PositionStatus.Closed)
            throw new InvalidOperationException("Position is already closed.");

        ExitPrice = exitPrice;
        CurrentPrice = exitPrice;
        Status = PositionStatus.Closed;
        ClosedAt = DateTime.UtcNow;
        MarkUpdated();
    }

    private decimal CalculatePnL(decimal price)
    {
        // Sell (short) = profit when price goes down; Buy (long) = profit when price goes up
        return Side == OrderSide.Sell
            ? (EntryPrice - price) * Quantity
            : (price - EntryPrice) * Quantity;
    }
}
