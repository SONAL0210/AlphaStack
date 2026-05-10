using AlphaStack.Domain.Enums;

namespace AlphaStack.Domain.Exceptions;

public class InvalidTradeTransitionException : Exception
{
    public TradeStatus Current { get; }
    public TradeStatus Attempted { get; }

    public InvalidTradeTransitionException(TradeStatus current, TradeStatus attempted)
        : base($"Cannot transition Trade from {current} to {attempted}.")
    {
        Current = current;
        Attempted = attempted;
    }
}
