using AlphaStack.Domain.Enums;

namespace AlphaStack.Domain.Entities;

/// <summary>
/// Immutable signal emitted by a strategy engine.
/// One signal = one multi-leg trade opportunity.
/// All legs share the same SignalGroupId.
/// </summary>
public record StrategySignal(
    Guid SignalGroupId,
    Guid StrategyExecutionId,
    string StrategyType,
    SignalAction Action,           // Enter, Exit, Adjust
    ExecutionMode Mode,
    IReadOnlyList<SignalLeg> Legs,
    string Rationale,              // Human-readable reason — goes to Telegram message
    DateTime GeneratedAt,
    decimal Vix = 0m,             // India VIX at signal time — 0 for exit signals
    decimal SpotAtSignal = 0m,    // Nifty spot at signal time — for SpotAtExit on exit signals
    decimal Ema20 = 0m,             // EMA20 at signal time — for MarketRegime calculation
    decimal Adr = 0m,     
    decimal Atr = 0m,
    decimal AtrAverage = 0m,
    decimal GapPercent = 0m);        

/// <summary>
/// One leg of a multi-leg signal (e.g. short put leg of a Bull Put Spread).
/// </summary>
public record SignalLeg(
    string TradingSymbol,
    string Exchange,
    int InstrumentToken,
    OrderSide Side,
    int Quantity,
    decimal LastPrice,             // LTP at signal time — used as reference for Telegram message
    OptionType? OptionType,
    decimal? StrikePrice,
    DateOnly? ExpiryDate);
