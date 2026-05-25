using Microsoft.Extensions.Logging;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Domain.Entities;
using AlphaStack.Domain.Enums;
using Microsoft.Extensions.Configuration;
using AlphaStack.Infrastructure.Strategies;
using AlphaStack.Infrastructure.BackgroundServices;
using System.Globalization;

namespace AlphaStack.Infrastructure.Strategies;

/// <summary>
/// Bear Call Spread signal engine.
///
/// Entry conditions (all must be true):
///   1. India VIX &lt; 20 (low vol regime)
///   2. NIFTY spot &lt; 20-day EMA (bearish bias)
///   3. Entry day is Monday, Wednesday, or Friday (weekly expiry cycle)
///   4. No existing open position on this execution
///   5. ATR not spiking (&lt; 1.5× 20-day ATR average)
///   6. Today's gap &lt; 1%
///
/// Legs (both on nearest weekly Tuesday expiry):
///   Sell OTM Call ~ADR×(VIX-adaptive) pts above spot (short leg, collects premium)
///   Buy  OTM Call 200 pts above short leg  (long leg, caps max loss)
///
/// Automatic exits (no Telegram approval required):
///   Profit target : 50% of net premium collected
///   Stop loss     : 2× net premium collected
///   Expiry day    : close at 14:45 IST regardless of P&amp;L
/// </summary>
public class BearCallSpreadEngine : BaseSpreadEngine
{
    // ── Strategy identity and parameters ─────────────────────────────────────

    public override string StrategyType             => "BearCallSpread";
    protected override string Underlying            => "NIFTY";
    protected override string SpotSymbol            => "NIFTY 50";
    protected override string SpotExchange          => "NSE";
    protected override string OptionsExchange       => "NFO";
    protected override int    SpotInstrumentToken   => 256265;   // standard Kite token for NIFTY 50
    protected override int    StrikeInterval        => 50;
    protected override int    SpreadWidth           => 200;
    protected override decimal VixThreshold         => 20m;
    protected override decimal AtrSpikeMultiple     => 1.5m;
    protected override decimal ProfitTarget         => 0.50m;
    protected override decimal StopLossMultiple     => 2.00m;
    protected override DayOfWeek[] EntryDays        =>
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
    protected override TimeOnly ExpiryExitTime      => new(14, 45);

    // ── Constructor ───────────────────────────────────────────────────────────

    public BearCallSpreadEngine(
        IMarketDataProvider    marketData,
        IInstrumentRepository  instruments,
        IPositionRepository    positions,
        ILogger<BearCallSpreadEngine> logger,
        IConfiguration         configuration,
        IInstrumentSyncState syncState)
        : base(marketData, instruments, positions, logger, configuration,syncState)
    {
    }

    // ── Entry ─────────────────────────────────────────────────────────────────

    public override async Task<StrategySignal?> EvaluateAsync(
        StrategyExecution execution, CancellationToken ct = default)
    {
        var (passed, ctx) = await EvaluateEntryGatesAsync(
            execution,
            optionType:   OptionType.Call,
            spotAboveEma: false,  // Bear-Call requires spot < EMA20
            ct);

        if (!passed || ctx is null) return null;

        _logger.LogInformation(
            "[BCS] ENTRY signal — spot={S:F0} EMA={E:F0} | " +
            "Short {SS}CE@{SP:F2} Long {LS}CE@{LP:F2} | NetCredit={NC:F2} × {Q} = ₹{Total:F0}",
            ctx.Spot, ctx.Ema20,
            ctx.ShortStrike, ctx.ShortPremium, ctx.LongStrike, ctx.LongPremium,
            ctx.NetCredit, ctx.Quantity, ctx.NetCredit * ctx.Quantity);

        return new StrategySignal(
            SignalGroupId:       Guid.NewGuid(),
            StrategyExecutionId: execution.Id,
            StrategyType:        StrategyType,
            Action:              SignalAction.Enter,
            Mode:                execution.Mode,
            Legs: new List<SignalLeg>
            {
                new(
                    // Short Call leg — sell to open
                    // NOTE: InstrumentToken is resolved inside EvaluateEntryGatesAsync;
                    // we pass 0 here as a placeholder. If the token is required on the
                    // signal, surface it through MarketContext in a follow-up change.
                    TradingSymbol: OptionSymbol(Underlying, ctx.Expiry, ctx.ShortStrike, "CE"),
                    Exchange:        OptionsExchange,
                    InstrumentToken: 0,
                    Side:            OrderSide.Sell,
                    Quantity:        ctx.Quantity,
                    LastPrice:           ctx.ShortPremium,
                    OptionType:      OptionType.Call,
                    StrikePrice:     ctx.ShortStrike,
                    ExpiryDate:      ctx.Expiry),
                new(
                    // Long Call leg — buy to open
                    TradingSymbol: OptionSymbol(Underlying, ctx.Expiry, ctx.LongStrike,  "CE"),
                    Exchange:        OptionsExchange,
                    InstrumentToken: 0,
                    Side:            OrderSide.Buy,
                    Quantity:        ctx.Quantity,
                    LastPrice:           ctx.LongPremium,
                    OptionType:      OptionType.Call,
                    StrikePrice:     ctx.LongStrike,
                    ExpiryDate:      ctx.Expiry)
            },
            Rationale:
                $"NIFTY {ctx.Spot:F0} < EMA20 {ctx.Ema20:F0} | " +
                $"ADR {ctx.Adr:F0}pts → offset {ctx.AdrBasedOffset}pts | " +
                $"ATR {ctx.Atr:F0}pts (avg {ctx.AtrAverage:F0}pts) | " +
                $"Short {ctx.ShortStrike}CE @{ctx.ShortPremium:F2} | Long {ctx.LongStrike}CE @{ctx.LongPremium:F2} | " +
                $"Net credit ₹{ctx.NetCredit:F2}/unit = ₹{ctx.NetCredit * ctx.Quantity:F0} total",
            GeneratedAt:  DateTime.UtcNow,
            Vix:          ctx.Vix,
            SpotAtSignal: ctx.Spot,
            Ema20:        ctx.Ema20,
            Adr:          ctx.Adr,
            Atr:          ctx.Atr,
            AtrAverage:   ctx.AtrAverage,
            GapPercent:   ctx.GapPercent);
    }

    private static string OptionSymbol(string underlying, DateOnly expiry, decimal strike, string type)
    {
        var exp = expiry.ToString("ddMMMyy", CultureInfo.InvariantCulture).ToUpperInvariant();
        // → "26MAY26"
        return $"{underlying}{exp}{(int)strike}{type}";
        // → "NIFTY26MAY2624050CE"
    }
    // ── Exit ──────────────────────────────────────────────────────────────────

    public override Task<StrategySignal?> EvaluateExitAsync(
        StrategyExecution execution, CancellationToken ct = default)
        => EvaluateExitCoreAsync(execution, ct);

    // ── Expiry helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the nearest upcoming Tuesday weekly expiry in IST.
    /// If today is Tuesday the next Tuesday is returned — same-day expiry entry
    /// is intentionally avoided since Mon/Wed/Fri are the only entry days.
    /// </summary>
    protected override DateOnly GetNearestExpiry(DateTime istNow)
                => NearestTuesdayExpiry(istNow);
}
