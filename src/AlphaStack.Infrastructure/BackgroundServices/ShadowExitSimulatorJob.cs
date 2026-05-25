// ── ADD THIS CLASS TO PnLTrackerService.cs OR AS A SEPARATE FILE ─────────────
// Inject IShadowTradeRepository + IMarketDataProvider (already in PnLTracker scope)
// Call RunShadowExitSimulatorAsync(scope, ct) at the END of RunTrackerCycleAsync

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AlphaStack.Application.Common.Interfaces;

namespace AlphaStack.Infrastructure.BackgroundServices;

/// <summary>
/// Runs inside PnLTrackerService every 5-minute cycle.
/// For each open shadow trade, checks if profit target or stop loss would have been hit
/// using current market LTP, then closes the record with outcome.
///
/// No real orders, no Telegram — purely a DB update.
/// </summary>
public static class ShadowExitSimulatorJob
{
    public static async Task RunAsync(IServiceScope scope, ILogger logger, CancellationToken ct)
    {
        try
        {
            var shadowRepo = scope.ServiceProvider.GetRequiredService<IShadowTradeRepository>();
            var marketData = scope.ServiceProvider.GetRequiredService<IMarketDataProvider>();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var syncState = scope.ServiceProvider.GetRequiredService<IInstrumentSyncState>();
            if (!syncState.IsReady)
            {
                logger.LogInformation("[ShadowExit] Skipping — instrument sync not ready yet.");
                return;
            }
            if (syncState.LastSyncWasSynthetic)
            {
                logger.LogWarning("[ShadowExit] Skipping — last sync was synthetic. Waiting for real sync.");
                return;
            }

            var openTrades = await shadowRepo.GetOpenAsync(ct);
            if (openTrades.Count == 0) return;

            logger.LogDebug("[ShadowExit] Evaluating {Count} open shadow trades", openTrades.Count);

            // Group by (ShortStrike, LongStrike, ExpiryDate) to minimise LTP fetches
            // Multiple variants share strikes — fetch once per unique pair
            var strikeGroups = openTrades
                .GroupBy(t => (t.ShortStrike, t.LongStrike, t.ExpiryDate))
                .ToList();

            foreach (var group in strikeGroups)
            {
                try
                {
                    // Fetch current spread value from market
                    // We approximate: spread LTP ≈ short put LTP - long put LTP
                    // Since we don't have individual leg LTPs in shadow trades,
                    // we use the short strike LTP as a proxy and estimate spread
                    var shortSymbol = BuildOptionSymbol(
                        group.First().StrategyName, group.Key.ShortStrike,
                        group.Key.ExpiryDate, isShort: true);

                    var shortQuote = await marketData.GetQuoteAsync(shortSymbol, "NFO", ct);
                    if (shortQuote is null) continue;

                    var longSymbol = BuildOptionSymbol(
                        group.First().StrategyName, group.Key.LongStrike,
                        group.Key.ExpiryDate, isShort: false);

                    var longQuote = await marketData.GetQuoteAsync(longSymbol, "NFO", ct);
                    if (longQuote is null) continue;

                    var currentSpreadValue = Math.Abs(shortQuote.LastPrice - longQuote.LastPrice);

                    foreach (var shadow in group)
                    {
                        var currentPnL = (shadow.PremiumCollected - currentSpreadValue) * GetQuantity(shadow.StrategyName);

                        string? exitReason = null;

                        if (currentPnL >= shadow.ProfitTargetRs)
                            exitReason = $"ProfitTarget{(int)(shadow.ProfitTargetPct * 100)}%";
                        else if (currentPnL <= -shadow.StopLossThresholdRs)
                            exitReason = $"StopLoss{shadow.StopLossMultiplier}x";
                        else if (shadow.ExpiryDate <= DateOnly.FromDateTime(DateTime.UtcNow))
                            exitReason = "ExpiryClose";

                        if (exitReason is null) continue;

                        shadow.CloseWithOutcome(exitReason, currentSpreadValue, GetQuantity(shadow.StrategyName));
                        await shadowRepo.UpdateAsync(shadow, ct);

                        logger.LogDebug(
                            "[ShadowExit] Closed | Strike={S}/{L} Outcome={O} PnL=₹{P:F0} Reason={R}",
                            shadow.ShortStrike, shadow.LongStrike,
                            shadow.Outcome, shadow.GrossPnL, exitReason);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "[ShadowExit] Failed for strike group {S}/{L}",
                        group.Key.ShortStrike, group.Key.LongStrike);
                }
            }

            await uow.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Shadow exit simulator must never affect real PnL tracker
            logger.LogWarning(ex, "[ShadowExit] Simulator cycle failed — PnL tracker unaffected");
        }
    }

    /// <summary>
    /// Builds a trading symbol matching the format used by BullPutSpreadEngine / BearCallSpreadEngine.
    /// e.g. NIFTY26512{strike}PE or FINNIFTY26W{strike}CE
    /// Adjust format to match what your InstrumentSyncService stores in TradingSymbol.
    /// </summary>
    // Replace the existing BuildOptionSymbol method:

    private static string BuildOptionSymbol(
    string strategyName, decimal strike, DateOnly expiry, bool isShort)
    {
        var underlying = strategyName.StartsWith("FINNIFTY", StringComparison.OrdinalIgnoreCase)
            ? "FINNIFTY" : "NIFTY";

        // Handle IronCondor wing variants
        var suffix = (strategyName.Contains("BullPut", StringComparison.OrdinalIgnoreCase) ||
                    strategyName.EndsWith("_Put", StringComparison.OrdinalIgnoreCase))
            ? "P" : "C";

        var yy = expiry.Year.ToString()[2..];
        var mm = expiry.Month.ToString("D2");
        var dd = expiry.Day.ToString("D2");
        return $"{underlying}{yy}{mm}{dd}{suffix}{(int)strike}";
    }

    private static int GetQuantity(string strategyName)
        => strategyName.StartsWith("FINNIFTY", StringComparison.OrdinalIgnoreCase) ? 40 : 65;
}