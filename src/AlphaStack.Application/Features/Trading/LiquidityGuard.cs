using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Domain.Entities;

namespace AlphaStack.Application.Features.Trading;

/// <summary>
/// Phase 1 guardrail for LiveOrderExecutor — checks each leg of a signal has
/// a live, tradeable bid-ask spread before a real order is placed. Read-only,
/// no side effects. Checked in addition to (not instead of) RiskManager's
/// capital/drawdown rules — this guards execution quality, RiskManager guards
/// exposure.
///
/// MaxSpreadPercent default (3%) is a starting guess, NOT validated against
/// real NIFTY option spreads yet — tune this once live orders are actually
/// being placed and real spread data is observed. Config key:
/// LiveTrading:MaxSpreadPercent
/// </summary>
public class LiquidityGuard : ILiquidityGuard
{
    private readonly IMarketDataProvider _marketData;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LiquidityGuard> _logger;

    public LiquidityGuard(
        IMarketDataProvider marketData,
        IConfiguration configuration,
        ILogger<LiquidityGuard> logger)
    {
        _marketData = marketData;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<RiskValidationResult> ValidateSignalLiquidityAsync(
        StrategySignal signal,
        CancellationToken ct = default)
    {
        var maxSpreadPercent = _configuration.GetValue<decimal?>("LiveTrading:MaxSpreadPercent") ?? 3.0m;

        foreach (var leg in signal.Legs)
        {
            var quote = await _marketData.GetQuoteAsync(leg.TradingSymbol, leg.Exchange, ct);

            if (quote is null)
            {
                var reason = $"No live quote available for {leg.TradingSymbol} — " +
                             "cannot verify liquidity before a live order.";
                _logger.LogWarning("[LiquidityGuard] BLOCKED | SignalGroup={Id} | {Reason}",
                    signal.SignalGroupId, reason);
                return RiskValidationResult.Reject(reason);
            }

            // Zero bid/ask means Fyers isn't reporting a real market for this
            // leg right now (seen intermittently on index quotes in logs) —
            // treated as unsafe to trade live, not as "spread is 0%".
            if (quote.BidPrice <= 0 || quote.AskPrice <= 0)
            {
                var reason = $"{leg.TradingSymbol} has no live bid/ask (bid={quote.BidPrice}, " +
                             $"ask={quote.AskPrice}) — refusing live entry on unknown liquidity.";
                _logger.LogWarning("[LiquidityGuard] BLOCKED | SignalGroup={Id} | {Reason}",
                    signal.SignalGroupId, reason);
                return RiskValidationResult.Reject(reason);
            }

            var mid = (quote.BidPrice + quote.AskPrice) / 2m;
            var spreadPercent = mid > 0
                ? (quote.AskPrice - quote.BidPrice) / mid * 100m
                : decimal.MaxValue;

            if (spreadPercent > maxSpreadPercent)
            {
                var reason = $"{leg.TradingSymbol} spread {spreadPercent:F2}% exceeds max " +
                             $"{maxSpreadPercent:F2}% (bid={quote.BidPrice}, ask={quote.AskPrice}).";
                _logger.LogWarning("[LiquidityGuard] BLOCKED | SignalGroup={Id} | {Reason}",
                    signal.SignalGroupId, reason);
                return RiskValidationResult.Reject(reason);
            }
        }

        _logger.LogInformation(
            "[LiquidityGuard] ALLOWED | SignalGroup={Id} | {LegCount} legs checked",
            signal.SignalGroupId, signal.Legs.Count);

        return RiskValidationResult.Allow();
    }
}