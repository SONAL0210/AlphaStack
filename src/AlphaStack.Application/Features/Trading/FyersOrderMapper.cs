// Features/Trading/FyersOrderMapper.cs — new file

using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Domain.Entities;
using AlphaStack.Domain.Enums;

namespace AlphaStack.Application.Features.Trading;

/// <summary>
/// Pure mapping from our domain signal shape into Fyers' multileg order
/// request. No I/O — testable without mocks.
///
/// KNOWN GAP (Aug 2026): callers must supply the correct Fyers-native
/// order-placement symbol per leg (e.g. "NSE:NIFTY26JUN2623750CE"), NOT
/// SignalLeg.TradingSymbol (our internal format, e.g. "NIFTY260616C23750").
/// No code path in the codebase currently resolves internal→Fyers-native
/// symbol for options at order-placement time — GetOptionQuoteFromChainAsync
/// fetches the whole chain and filters locally rather than constructing a
/// symbol string. This must be solved before PlaceMultilegOrderAsync can be
/// called from a real strategy signal. See DECISIONS.md.
/// </summary>
public static class FyersOrderMapper
{
    /// <param name="fyersSymbolsInLegOrder">
    /// Correct Fyers-native symbol for each leg, in the same order as
    /// signal.Legs — caller's responsibility until the gap above is closed.
    /// </param>
    /// <param name="limitPricesInLegOrder">
    /// Marketable-limit price per leg (current bid for sell legs, current
    /// ask for buy legs — per the earlier pricing-strategy decision),
    /// same order as signal.Legs.
    /// </param>
    public static FyersMultilegOrderRequest ToMultilegRequest(
        StrategySignal signal,
        IReadOnlyList<string> fyersSymbolsInLegOrder,
        IReadOnlyList<decimal> limitPricesInLegOrder,
        string productType = "MARGIN")
    {
        if (signal.Legs.Count is < 2 or > 3)
            throw new ArgumentException(
                $"Multileg supports 2 or 3 legs only. Signal {signal.SignalGroupId} has {signal.Legs.Count}.");

        if (fyersSymbolsInLegOrder.Count != signal.Legs.Count || limitPricesInLegOrder.Count != signal.Legs.Count)
            throw new ArgumentException("Symbol/price arrays must match signal leg count exactly.");

        var legs = signal.Legs.Select((leg, i) => new FyersMultilegLeg(
            Symbol: fyersSymbolsInLegOrder[i],
            Quantity: leg.Quantity,
            Side: leg.Side == OrderSide.Buy ? 1 : -1,
            LimitPrice: limitPricesInLegOrder[i]
        )).ToList();

        var orderType = signal.Legs.Count == 2 ? "2L" : "3L";

        // Confirmed live: hyphens are rejected in orderTag. Same convention
        // already used in PaperOrderSimulator's ClientOrderId fallback.
        var orderTag = signal.SignalGroupId.ToString("N");

        return new FyersMultilegOrderRequest(orderTag, productType, orderType, legs);
    }

    /// <summary>
    /// Produces an ORDERED list of individual FyersOrderRequests for
    /// sequential placement — Buy leg(s) first, then Sell leg(s). This
    /// ordering is not cosmetic: per direct confirmation from Fyers'
    /// Product Manager (Aug 2026, see DECISIONS.md), the margin hedge
    /// benefit on the Multi-Order API only applies when the buy/hedge leg
    /// is placed before the sell leg. Reversing this order forfeits the
    /// margin benefit even though both legs still get placed.
    ///
    /// This method only determines ORDER — it does not place anything or
    /// decide whether to wait for leg-1 confirmation before firing leg-2.
    /// That orchestration (and the exposure-window question it raises) is
    /// LiveOrderExecutor's responsibility, not this mapper's.
    /// </summary>
    public static IReadOnlyList<FyersOrderRequest> ToSequentialOrderRequests(
        StrategySignal signal,
        IReadOnlyList<string> fyersSymbolsInLegOrder,
        IReadOnlyList<decimal> limitPricesInLegOrder,
        string productType = "MARGIN",
        string validity = "DAY")
    {
        if (fyersSymbolsInLegOrder.Count != signal.Legs.Count || limitPricesInLegOrder.Count != signal.Legs.Count)
            throw new ArgumentException("Symbol/price arrays must match signal leg count exactly.");

        var indexed = signal.Legs
            .Select((leg, i) => (leg, symbol: fyersSymbolsInLegOrder[i], price: limitPricesInLegOrder[i]))
            .OrderBy(x => x.leg.Side == OrderSide.Buy ? 0 : 1) // Buy first, stable sort preserves original relative order within each side
            .ToList();

        return indexed.Select(x => new FyersOrderRequest(
            Symbol: x.symbol,
            Quantity: x.leg.Quantity,
            Type: 1, // Limit — marketable-limit-at-bid/ask, per the earlier pricing decision
            Side: x.leg.Side == OrderSide.Buy ? 1 : -1,
            ProductType: productType,
            LimitPrice: x.price,
            StopPrice: 0,
            Validity: validity,
            OrderTag: signal.SignalGroupId.ToString("N")
        )).ToList();
    }
}