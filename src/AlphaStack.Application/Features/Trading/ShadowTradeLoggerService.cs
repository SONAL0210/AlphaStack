using Microsoft.Extensions.Logging;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Domain.Entities;

namespace AlphaStack.Application.Features.Trading;

/// <summary>
/// Logs synthetic trade variants at every signal evaluation.
/// Called by SignalProcessor after a real entry signal is evaluated.
///
/// For each signal, generates a full parameter matrix:
///   5 ADR multipliers × 4 spread widths × 3 profit targets × 3 stop losses = 180 variants
///
/// Each variant uses the SAME market context (spot, VIX, ATR, expiry) as the real signal
/// but different strike/exit parameters. This accelerates data collection 180× without
/// requiring additional capital.
///
/// Fire-and-forget: failures are logged but never block real trade execution.
/// </summary>
public class ShadowTradeLoggerService
{


    // ── Parameter matrix ──────────────────────────────────────────────────────

    private static readonly decimal[] AdrMultipliers = { 1.0m, 1.25m, 1.5m, 1.75m, 2.0m };
    private static readonly int[] SpreadWidths = { 100, 150, 200, 250 };
    private static readonly decimal[] ProfitTargets = { 0.40m, 0.50m, 0.60m };
    private static readonly decimal[] StopLossMultiples = { 1.5m, 2.0m, 3.0m };

    private readonly IShadowTradeRepository _shadowRepo;
    private readonly IUnitOfWork _uow;
    private readonly IMarketDataProvider _marketData;
    private readonly ILogger<ShadowTradeLoggerService> _logger;

    public ShadowTradeLoggerService(
        IShadowTradeRepository shadowRepo,
        IUnitOfWork uow,
        IMarketDataProvider marketData,  // ADD THIS
        ILogger<ShadowTradeLoggerService> logger)
    {
        _shadowRepo = shadowRepo;
        _uow = uow;
        _marketData = marketData;
        _logger = logger;
    }

    /// <summary>
    /// Log all parameter variants for a signal evaluation. Always call this,
    /// even if the signal was rejected — the market context is still valid data.
    /// </summary>
    /// <param name="context">Market snapshot from the engine at evaluation time.</param>
    /// <param name="realSignalGroupId">The real trade's SignalGroupId, null if signal was rejected.</param>
    /// <param name="strategyName">e.g. "BullPutSpread"</param>
    /// <param name="entryVariation">e.g. "MondayEntry"</param>
    /// <param name="strikeInterval">50 for NIFTY, 100 for FINNIFTY</param>
    /// <param name="quantity">Lot size used.</param>
    /// <param name="realAdrMultiplier">The multiplier actually used in the real trade.</param>
    /// <param name="realSpreadWidth">The spread width actually used in the real trade.</param>
    public async Task LogVariantsAsync(
    ShadowMarketContext context,
    Guid? realSignalGroupId,
    string strategyName,
    string entryVariation,
    int strikeInterval,
    int quantity,
    decimal realAdrMultiplier,
    int realSpreadWidth,
    CancellationToken ct = default)
    {
        try
        {
            var ist = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
            var istNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ist);
            var timeNow = TimeOnly.FromDateTime(istNow);

            if (istNow.DayOfWeek == DayOfWeek.Saturday ||
                istNow.DayOfWeek == DayOfWeek.Sunday ||
                timeNow < new TimeOnly(9, 15) ||
                timeNow > new TimeOnly(15, 30))
            {
                _logger.LogInformation("[ShadowLogger] Skipped — outside market hours");
                return;
            }

            var isBullPut = strategyName.Contains("BullPut",  StringComparison.OrdinalIgnoreCase)
             || strategyName.EndsWith("_Put",      StringComparison.OrdinalIgnoreCase);
            var underlying = strategyName.StartsWith("FINNIFTY", StringComparison.OrdinalIgnoreCase)
                ? "FINNIFTY" : "NIFTY";
            var optSuffix = isBullPut ? "P" : "C";
            var expiryStr = context.Expiry.ToString("yyMMdd");

            // ── LTP cache — one chain fetch per underlying, reused across all 180 variants ──
            var ltpCache = new Dictionary<decimal, decimal>();

            async Task<decimal> GetLtpAsync(decimal strike)
            {
                if (ltpCache.TryGetValue(strike, out var cached))
                    return cached;

                var symbol = $"{underlying}{expiryStr}{optSuffix}{(int)strike}";
                var quote = await _marketData.GetQuoteAsync(symbol, "NFO", ct);
                var ltp = quote?.LastPrice ?? 0m;
                ltpCache[strike] = ltp;
                return ltp;
            }

            var variants = new List<ShadowTrade>();
            var evaluatedAt = DateTime.UtcNow;

            foreach (var adrMult in AdrMultipliers)
                foreach (var width in SpreadWidths)
                    foreach (var pt in ProfitTargets)
                        foreach (var sl in StopLossMultiples)
                        {
                            var adrOffset = Math.Max(
                                width,
                                (int)Math.Round(context.Adr * adrMult / strikeInterval) * strikeInterval);

                            decimal shortStrike, longStrike;
                            if (isBullPut)
                            {
                                // Bull Put: short below spot, long further below
                                shortStrike = RoundToInterval(context.Spot - adrOffset, strikeInterval);
                                longStrike = shortStrike - width;
                            }
                            else
                            {
                                // Bear Call: short above spot, long further above
                                shortStrike = RoundToInterval(context.Spot + adrOffset, strikeInterval);
                                longStrike = shortStrike + width;
                            }

                            // Fetch real market LTP for both legs
                            var shortLtp = await GetLtpAsync(shortStrike);
                            var longLtp = await GetLtpAsync(longStrike);

                            // Skip variant if market data unavailable or spread has no credit
                            if (shortLtp <= 0 || longLtp <= 0) continue;

                            var realPremium = Math.Round(shortLtp - longLtp, 2);
                            if (realPremium <= 0) continue;

                            var shadow = ShadowTrade.Create(
                                realSignalGroupId: realSignalGroupId,
                                strategyName: strategyName,
                                entryVariation: entryVariation,
                                wasRealTrade : realSignalGroupId.HasValue && adrMult == realAdrMultiplier && width == realSpreadWidth,
                                wasPositionBlocked: context.WasPositionBlocked,
                                marketRegimeValid:   context.MarketRegimeValid,
                                evaluatedAt: evaluatedAt,
                                spotAtEntry: context.Spot,
                                vixAtEntry: context.Vix,
                                vixRegime: context.VixRegime,
                                ema20AtEntry: context.Ema20,
                                adrAtEntry: context.Adr,
                                atrAtEntry: context.Atr,
                                atrAverageAtEntry: context.AtrAverage,
                                gapPercent: context.GapPercent,
                                daysToExpiry: context.DaysToExpiry,
                                expiryDate: context.Expiry,
                                adrMultiplierUsed: adrMult,
                                spreadWidth: width,
                                profitTargetPct: pt,
                                stopLossMultiplier: sl,
                                shortStrike: shortStrike,
                                longStrike: longStrike,
                                premiumCollected: realPremium,
                                quantity: quantity);

                            variants.Add(shadow);
                        }

            if (variants.Count == 0)
            {
                _logger.LogWarning(
                    "[ShadowLogger] No variants logged — all strikes had no market data | Strategy={S}",
                    strategyName);
                return;
            }

            await _shadowRepo.AddRangeAsync(variants, ct);
            await _uow.SaveChangesAsync(ct);

            _logger.LogInformation(
                "[ShadowLogger] Logged {Count} variants | Strategy={S} GroupId={G}",
                variants.Count, strategyName, realSignalGroupId);
        }
        catch (Exception ex)
        {
            // Shadow logging must NEVER affect real trade flow
            _logger.LogWarning(ex,
                "[ShadowLogger] Failed to log variants for GroupId={G} — real trade unaffected",
                realSignalGroupId);
        }
    }

    private static decimal RoundToInterval(decimal value, int interval)
        => Math.Round(value / interval) * interval;

    /// <summary>
    /// Market snapshot passed from the strategy engine to the shadow logger.
    /// Populated from MarketContext in BaseSpreadEngine after Task 1 refactor.
    /// </summary>
    public record ShadowMarketContext(
    decimal Spot,
    decimal Vix,
    string VixRegime,
    decimal Ema20,
    decimal Adr,
    decimal Atr,
    decimal AtrAverage,
    decimal GapPercent,
    int DaysToExpiry,
    DateOnly Expiry,
    decimal RealNetCredit,
    bool WasPositionBlocked = false,
    bool MarketRegimeValid = true); 
}
   
