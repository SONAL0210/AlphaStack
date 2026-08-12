using AlphaStack.Domain.Entities;
using AlphaStack.Domain.Enums;

namespace AlphaStack.Application.Features.Trading;

public static class TradingFeeCalculator
{
    // Options STT on sell-side premium. Raised from 0.1% to 0.15% effective
    // Apr 1, 2026 (Budget 2026). Was previously hard-coded inline at the old rate.
    private const decimal SttRate = 0.0015m;

    /// <summary>
    /// Full calculation using individual leg LTPs.
    /// Used in SignalProcessor for real/paper trades at exit.
    /// </summary>
    public static decimal ComputeForRealTrade(
        IReadOnlyList<SignalLeg> legs,
        int quantity)
    {
        var totalPremiumValue = legs.Sum(l => l.LastPrice * quantity) * 2;
        var flatBrokerage = 20m * legs.Count * 2;
        var exchangeFee = totalPremiumValue * 0.0005m;
        var sellPremium = legs
            .Where(l => l.Side == OrderSide.Sell)
            .Sum(l => l.LastPrice) * quantity;
        var stt = sellPremium * SttRate;
        var sebi = totalPremiumValue / 10_000_000m * 10m;
        var preTaxCharges = flatBrokerage + exchangeFee + sebi;
        var gst = preTaxCharges * 0.18m;

        return Math.Round(flatBrokerage + exchangeFee + stt + sebi + gst, 2);
    }

    /// <summary>
    /// Simplified calculation for shadow trades.
    /// Uses net premium at entry + exit leg prices (individual entry LTPs not stored).
    /// </summary>
    public static decimal ComputeForShadow(
        int legCount,
        decimal entryNetPremium,
        decimal exitShortLtp,
        decimal exitLongLtp,
        int quantity)
    {
        // Approximate total premium traded across entry + exit
        var exitPremium = (exitShortLtp + exitLongLtp) * quantity;
        var entryPremium = entryNetPremium * quantity; // net only — best available
        var totalPremiumValue = entryPremium + exitPremium;

        var flatBrokerage = 20m * legCount * 2;
        var exchangeFee = totalPremiumValue * 0.0005m;

        // STT on sell side — at entry we sold short strike, approximate with exit short LTP
        var stt = exitShortLtp * quantity * SttRate;
        var sebi = totalPremiumValue / 10_000_000m * 10m;
        var preTaxCharges = flatBrokerage + exchangeFee + sebi;
        var gst = preTaxCharges * 0.18m;

        return Math.Round(flatBrokerage + exchangeFee + stt + sebi + gst, 2);
    }
}