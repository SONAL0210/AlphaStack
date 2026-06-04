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
        IMarketDataProvider marketData,
        ILogger<ShadowTradeLoggerService> logger)
    {
        _shadowRepo = shadowRepo;
        _uow = uow;
        _marketData = marketData;
        _logger = logger;
    }

    /// <summary>
    /// Log all parameter variants for a signal evaluation.
    /// For Iron Condor, set pairedWingStrategyName to the opposite wing (e.g., "_Call").
    /// This ensures that each parameter combination produces a matching pair of rows
    /// with the same shadowGroupId, even when some strikes are skipped.
    /// </summary>
    public async Task LogVariantsAsync(
        ShadowMarketContext context,
        Guid? realSignalGroupId,
        string strategyName,
        string entryVariation,
        int strikeInterval,
        int quantity,
        decimal realAdrMultiplier,
        int realSpreadWidth,
        string? pairedWingStrategyName = null,
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

            var isPaired = !string.IsNullOrEmpty(pairedWingStrategyName);
            var isPrimaryBullPut = strategyName.Contains("BullPut") || strategyName.EndsWith("_Put");
            var isPairedBullPut = pairedWingStrategyName?.Contains("BullPut") == true || pairedWingStrategyName?.EndsWith("_Put") == true;

            var primarySuffix = isPrimaryBullPut ? "P" : "C";
            var pairedSuffix = isPairedBullPut ? "P" : "C";

            var underlying = strategyName.StartsWith("FINNIFTY", StringComparison.OrdinalIgnoreCase)
                ? "FINNIFTY" : "NIFTY";
            var expiryStr = context.Expiry.ToString("yyMMdd");

            // Separate LTP caches for each wing (strikes differ)
            var primaryLtpCache = new Dictionary<decimal, decimal>();
            var pairedLtpCache = new Dictionary<decimal, decimal>();

            async Task<decimal> GetLtpAsync(decimal strike, string suffix, Dictionary<decimal, decimal> cache)
            {
                if (cache.TryGetValue(strike, out var cached))
                    return cached;

                var symbol = $"{underlying}{expiryStr}{suffix}{(int)strike}";
                var quote = await _marketData.GetQuoteAsync(symbol, "NFO", ct);
                var ltp = quote?.LastPrice ?? 0m;
                cache[strike] = ltp;
                return ltp;
            }

            var variants = new List<ShadowTrade>();
            var evaluatedAt = DateTime.UtcNow;

            foreach (var adrMult in AdrMultipliers)
                foreach (var width in SpreadWidths)
                    foreach (var pt in ProfitTargets)
                        foreach (var sl in StopLossMultiples)
                        {
                            // Generate a new group ID for this specific variant pair (or single variant)
                            //var variantGroupId = Guid.NewGuid();
                            Guid? variantGroupId = isPaired ? Guid.NewGuid() : (Guid?)null;

                            // Build primary wing
                            var primary = await BuildVariantAsync(
                                context, adrMult, width, pt, sl, strikeInterval, quantity,
                                isPrimaryBullPut, primarySuffix, primaryLtpCache, GetLtpAsync,
                                realAdrMultiplier, realSpreadWidth, realSignalGroupId,
                                strategyName, entryVariation, evaluatedAt, variantGroupId);

                            if (primary == null) continue;

                            variants.Add(primary);

                            if (isPaired)
                            {
                                // Build paired wing (opposite side)
                                var paired = await BuildVariantAsync(
                                    context, adrMult, width, pt, sl, strikeInterval, quantity,
                                    !isPrimaryBullPut, pairedSuffix, pairedLtpCache, GetLtpAsync,
                                    realAdrMultiplier, realSpreadWidth, realSignalGroupId,
                                    pairedWingStrategyName!, entryVariation, evaluatedAt, variantGroupId);

                                if (paired == null)
                                {
                                    // If one wing is invalid, remove the already added primary to keep consistency
                                    variants.Remove(primary);
                                    continue;
                                }

                                variants.Add(paired);
                            }
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

    /// <summary>
    /// Builds a single shadow trade variant (one wing) without saving.
    /// Returns null if market data unavailable or premium non-positive.
    /// </summary>
    private async Task<ShadowTrade?> BuildVariantAsync(
        ShadowMarketContext context,
        decimal adrMult,
        int width,
        decimal profitTargetPct,
        decimal stopLossMultiplier,
        int strikeInterval,
        int quantity,
        bool isBullPut,
        string suffix,
        Dictionary<decimal, decimal> ltpCache,
        Func<decimal, string, Dictionary<decimal, decimal>, Task<decimal>> getLtp,
        decimal realAdrMultiplier,
        int realSpreadWidth,
        Guid? realSignalGroupId,
        string strategyName,
        string entryVariation,
        DateTime evaluatedAt,
        Guid? variantGroupId)
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

        var shortLtp = await getLtp(shortStrike, suffix, ltpCache);
        var longLtp = await getLtp(longStrike, suffix, ltpCache);

        if (shortLtp <= 0 || longLtp <= 0) return null;

        var premium = Math.Round(shortLtp - longLtp, 2);
        if (premium <= 0) return null;

        var wasRealTrade = realSignalGroupId.HasValue && adrMult == realAdrMultiplier && width == realSpreadWidth;

        return ShadowTrade.Create(
            realSignalGroupId: realSignalGroupId,
            shadowGroupId: variantGroupId,
            strategyName: strategyName,
            entryVariation: entryVariation,
            wasRealTrade: wasRealTrade,
            wasPositionBlocked: context.WasPositionBlocked,
            marketRegimeValid: context.MarketRegimeValid,
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
            profitTargetPct: profitTargetPct,
            stopLossMultiplier: stopLossMultiplier,
            shortStrike: shortStrike,
            longStrike: longStrike,
            premiumCollected: premium,
            quantity: quantity);
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