using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Domain.Enums;

namespace AlphaStack.Infrastructure.BackgroundServices;

/// <summary>
/// Runs inside PnLTrackerService every 5-minute cycle, after ShadowExitSimulatorJob.
///
/// Closes paper trades whose options have expired — i.e. all open positions belonging
/// to a trade have ExpiryDate <= today. Handles the case where EvaluateExitAsync never
/// fires a formal exit signal because premium decayed to near-zero without hitting the
/// stop-loss threshold.
///
/// For each expired trade:
///   1. Fetches current LTP for each open position leg (near-zero on expiry day, 0 after)
///   2. Closes each position via Position.Close(exitPrice, closedAt)
///   3. Closes the trade via Trade.ForceCloseAtExpiry(exitPrice, exitTime)
///      — RealizedPnL is computed inside the entity using ComputePnL()
///   4. Updates TradeAnalytics.CloseAnalytics() with final exit values
///
/// Fire-and-forget safe: failures are logged but never block PnLTracker.
/// </summary>
public static class PaperTradeExpiryCloserJob
{
    private static readonly TimeZoneInfo Ist =
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    public static async Task RunAsync(IServiceScope scope, ILogger logger, CancellationToken ct)
    {
        try
        {
            var tradeRepo     = scope.ServiceProvider.GetRequiredService<ITradeRepository>();
            var positionRepo  = scope.ServiceProvider.GetRequiredService<IPositionRepository>();
            var analyticsRepo = scope.ServiceProvider.GetRequiredService<ITradeAnalyticsRepository>();
            var marketData    = scope.ServiceProvider.GetRequiredService<IMarketDataProvider>();
            var executionRepo = scope.ServiceProvider.GetRequiredService<IStrategyExecutionRepository>();
            var uow           = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var today = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Ist));

            var runningExecutions = await executionRepo.GetRunningExecutionsAsync(ct);

            foreach (var execution in runningExecutions)
            {
                var openTrades = await tradeRepo.GetOpenByExecutionAsync(execution.Id, ct);

                foreach (var trade in openTrades)
                {
                    try
                    {
                        var positions = await positionRepo.GetBySignalGroupAsync(
                            trade.EntrySignalGroupId, ct);

                        var openLegs = positions
                            .Where(p => p.Status == PositionStatus.Open)
                            .ToList();

                        if (!openLegs.Any()) continue;

                        // Only act when ALL open legs have expired
                        var allExpired = openLegs.All(p =>
                            p.ExpiryDate.HasValue && p.ExpiryDate.Value <= today);

                        if (!allExpired) continue;

                        logger.LogInformation(
                            "[PaperExpiryCloser] Closing expired trade {TradeId} | " +
                            "Symbol={Symbol} Expiry={Expiry} Legs={Count}",
                            trade.Id, trade.Symbol,
                            openLegs.First().ExpiryDate, openLegs.Count);

                        // ── 1. Fetch exit LTPs ────────────────────────────────────────────

                        var exitPrices = new Dictionary<Guid, decimal>();

                        foreach (var leg in openLegs)
                        {
                            await Task.Delay(250, ct);
                            try
                            {
                                var quote = await marketData.GetQuoteAsync(
                                    leg.TradingSymbol, leg.Exchange.ToString(), ct);

                                exitPrices[leg.Id] = quote?.LastPrice ?? 0m;

                                logger.LogInformation(
                                    "[PaperExpiryCloser] {Symbol} {Side} Entry=₹{Entry:F2} Exit=₹{Exit:F2}",
                                    leg.TradingSymbol, leg.Side,
                                    leg.EntryPrice, exitPrices[leg.Id]);
                            }
                            catch (Exception ex)
                            {
                                exitPrices[leg.Id] = 0m;
                                logger.LogWarning(ex,
                                    "[PaperExpiryCloser] LTP fetch failed for {Symbol} — using 0",
                                    leg.TradingSymbol);
                            }
                        }

                        // ── 2. Close each position leg ────────────────────────────────────

                        foreach (var leg in openLegs)
                        {
                            leg.Close(exitPrices[leg.Id]);
                            await positionRepo.UpdateAsync(leg, ct);
                        }

                        // ── 3. Close the parent trade via ForceCloseAtExpiry ──────────────
                        // exitPrice on trade = short leg exit LTP (representative value)
                        // RealizedPnL is computed inside Trade.ComputePnL() using Direction

                        var shortLeg = openLegs.FirstOrDefault(p => p.Side == OrderSide.Sell);
                        var tradeExitPrice = shortLeg is not null
                            ? exitPrices[shortLeg.Id]
                            : 0m;

                        trade.ForceCloseAtExpiry(tradeExitPrice, DateTime.UtcNow);
                        await tradeRepo.UpdateAsync(trade, ct);

                        // ── 4. Close TradeAnalytics ───────────────────────────────────────

                        try
                        {
                            var analytics = await analyticsRepo.GetByTradeIdAsync(
                                trade.EntrySignalGroupId, ct);

                            if (analytics is not null)
                            {
                                // Compute gross PnL manually here for analytics
                                // (trade.RealizedPnL is now set, but we need it for CloseAnalytics)
                                var grossPnL = trade.RealizedPnL ?? 0m;

                                // premiumCaptured = what we bought back the spread for at exit
                                // For expiry: short leg exits near 0, long leg exits near 0
                                // Net spread value at exit ≈ shortExitLtp - longExitLtp
                                var longLeg = openLegs.FirstOrDefault(p => p.Side == OrderSide.Buy);
                                var longExitLtp = longLeg is not null ? exitPrices[longLeg.Id] : 0m;
                                var premiumCaptured = Math.Max(0m,
                                    (shortLeg is not null ? exitPrices[shortLeg.Id] : 0m) - longExitLtp);

                                // Estimate brokerage: 2 legs × ₹20 flat paper fee
                                var brokerage = openLegs.Count * 20m;

                                analytics.CloseAnalytics(
                                    spotAtExit: 0m,          // spot not fetched here — acceptable for expiry close
                                    exitVariation: "ExpiryClose",
                                    exitReason: "ExpiryClose",
                                    premiumCaptured: premiumCaptured,
                                    grossPnL: grossPnL,
                                    brokerage: brokerage,
                                    entryTime: new DateTimeOffset(trade.EntryTime ?? trade.CreatedAt),
                                    exitTime: DateTimeOffset.UtcNow);

                                await analyticsRepo.UpdateAsync(analytics, ct);
                            }
                        }
                        catch (Exception ex)
                        {
                            // Analytics failure must never block trade closure
                            logger.LogWarning(ex,
                                "[PaperExpiryCloser] Analytics update failed for {TradeId} — trade still closed",
                                trade.Id);
                        }

                        logger.LogInformation(
                            "[PaperExpiryCloser] ✅ Closed at expiry | TradeId={TradeId} " +
                            "RealizedPnL=₹{PnL:F0}",
                            trade.Id, trade.RealizedPnL ?? 0m);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex,
                            "[PaperExpiryCloser] Failed to close trade {TradeId} — will retry next cycle",
                            trade.Id);
                    }
                }
            }

            await uow.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[PaperExpiryCloser] Cycle failed — PnL tracker unaffected");
        }
    }
}
