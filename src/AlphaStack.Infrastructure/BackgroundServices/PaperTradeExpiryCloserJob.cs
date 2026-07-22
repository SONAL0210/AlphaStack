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

                        var allExpired = openLegs.All(p =>
                                p.ExpiryDate.HasValue && p.ExpiryDate.Value < today);

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
                        // NOTE: Trade only stores a single representative EntryPrice/ExitPrice
                        // (first short leg found). For 2-leg spreads this undercounts by ignoring
                        // the long leg; for IC it's ambiguous (two short legs exist). Trade.RealizedPnL
                        // is a known-limited value — TradeAnalytics.GrossPnL (computed below,
                        // independently) remains the source of truth. Do not read trade.RealizedPnL
                        // for P&L reporting anywhere.

                        var shortLeg = openLegs.FirstOrDefault(p => p.Side == OrderSide.Sell);
                        var tradeExitPrice = shortLeg is not null
                            ? exitPrices[shortLeg.Id]
                            : 0m;

                        trade.ForceCloseAtExpiry(tradeExitPrice, DateTime.UtcNow);
                        await tradeRepo.UpdateAsync(trade, ct);

                        // ── 4. Close TradeAnalytics — computed independently, not from trade.RealizedPnL ──

                        try
                        {
                            var analytics = await analyticsRepo.GetByTradeIdAsync(
                                trade.EntrySignalGroupId, ct);

                            if (analytics is not null)
                            {
                                var isIronCondor = analytics.StrategyName.Contains("IronCondor");
                                var referenceQty = openLegs.First().Quantity;

                                decimal exitSpreadValue;
                                if (isIronCondor)
                                {
                                    var putShort = openLegs.FirstOrDefault(p =>
                                        p.Side == OrderSide.Sell && p.OptionType == OptionType.Put);
                                    var putLong = openLegs.FirstOrDefault(p =>
                                        p.Side == OrderSide.Buy && p.OptionType == OptionType.Put);
                                    var callShort = openLegs.FirstOrDefault(p =>
                                        p.Side == OrderSide.Sell && p.OptionType == OptionType.Call);
                                    var callLong = openLegs.FirstOrDefault(p =>
                                        p.Side == OrderSide.Buy && p.OptionType == OptionType.Call);

                                    var putExitVal = Math.Abs(
                                        (putShort is not null ? exitPrices[putShort.Id] : 0m) -
                                        (putLong is not null ? exitPrices[putLong.Id] : 0m));
                                    var callExitVal = Math.Abs(
                                        (callShort is not null ? exitPrices[callShort.Id] : 0m) -
                                        (callLong is not null ? exitPrices[callLong.Id] : 0m));

                                    exitSpreadValue = putExitVal + callExitVal;
                                }
                                else
                                {
                                    var longLeg = openLegs.FirstOrDefault(p => p.Side == OrderSide.Buy);
                                    exitSpreadValue = shortLeg is not null && longLeg is not null
                                        ? Math.Abs(exitPrices[shortLeg.Id] - exitPrices[longLeg.Id])
                                        : 0m;
                                }

                                // Same formula as UpdateAnalyticsAtExitAsync — premiumCaptured is
                                // NOT maxed at this stage; grossPnL uses the raw (possibly negative) value.
                                var premiumCapturedRaw = analytics.PremiumCollected - exitSpreadValue;
                                var grossPnL  = premiumCapturedRaw * referenceQty;
                                var brokerage = openLegs.Count * 20m; // paper flat fee
                                var netPnL    = grossPnL - brokerage;

                                analytics.CloseAnalytics(
                                    spotAtExit: 0m,
                                    exitVariation: "ExpiryClose",
                                    exitReason: "ExpiryClose",
                                    premiumCaptured: Math.Max(0m, premiumCapturedRaw),
                                    grossPnL: grossPnL,
                                    brokerage: brokerage,
                                    entryTime: new DateTimeOffset(trade.EntryTime ?? trade.CreatedAt),
                                    exitTime: DateTimeOffset.UtcNow);

                                await analyticsRepo.UpdateAsync(analytics, ct);

                                // Roll up net PnL into execution — feeds RiskManager's
                                // drawdown guard (Rule 3). Previously never called; execution.RealizedPnL
                                // stayed at 0 and the guard was dead code.
                                execution.RecordFilledTrade(netPnL);
                                await executionRepo.UpdateAsync(execution, ct);
                            }
                            else
                            {
                                logger.LogWarning(
                                    "[PaperExpiryCloser] No analytics found for {TradeId} — " +
                                    "skipping execution PnL rollup (trade.RealizedPnL is not a safe fallback)",
                                    trade.Id);
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex,
                                "[PaperExpiryCloser] Analytics update failed for {TradeId} — trade still closed",
                                trade.Id);
                        }

                        logger.LogInformation(
                            "[PaperExpiryCloser] ✅ Closed at expiry | TradeId={TradeId}",
                            trade.Id);
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