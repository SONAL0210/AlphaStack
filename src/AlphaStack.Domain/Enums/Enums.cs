namespace AlphaStack.Domain.Enums;

public enum StrategyLifecycleStage
{
    Hypothesis,
    Backtesting,
    PaperTrading,
    Live,
    Deprecated
}

public enum ExecutionMode
{
    Paper,
    Live
}

public enum OrderSide
{
    Buy,
    Sell
}

public enum OrderStatus
{
    Pending,            // Signal generated, awaiting Telegram approval
    Approved,           // Approved via Telegram, ready to route
    Rejected,           // Rejected via Telegram
    Placed,             // Sent to broker / simulated
    Filled,
    PartiallyFilled,
    Cancelled,
    Failed
}

public enum OrderType
{
    Market,
    Limit,
    StopLoss,
    StopLossMarket
}

public enum InstrumentType
{
    Equity,
    FuturesAndOptions,
    Currency,
    Commodity
}

public enum OptionType
{
    Call,
    Put
}

public enum PositionStatus
{
    Open,
    Closed
}

public enum SignalAction
{
    Enter,
    Exit,
    Adjust
}

public enum TradeStatus
{
    Created,        // Trade record created, entry not yet submitted
    EntryPending,   // Entry orders placed, waiting for fill confirmation
    Entered,        // Entry filled, position is live
    ExitPending,    // Exit orders placed, waiting for fill
    Closed,         // Exit filled, RealizedPnL recorded
    Failed          // Entry or exit failed after all retries
}

public enum TradeDirection
{
    Long,
    Short
}
