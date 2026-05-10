using Microsoft.Extensions.Logging;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Domain.Entities;
using AlphaStack.Domain.Enums;
using Microsoft.Extensions.Configuration;

namespace AlphaStack.Infrastructure.Strategies;

/// <summary>
/// Bull Put Spread signal engine for BANKNIFTY.
///
/// Entry conditions (all must be true):
///   1. India VIX &lt; 20 (low vol regime)
///   2. BANKNIFTY spot &gt; 20-day EMA (bullish bias)
///   3. Entry day is Monday, Wednesday, or Friday
///   4. No existing open position on this execution
///   5. ATR not spiking (&lt; 1.5× 20-day ATR average)
///   6. Today's gap &lt; 1%
///
/// Legs (both on nearest weekly Wednesday expiry):
///   Sell OTM Put  ~ADR×(VIX-adaptive+0.2) pts below spot (short leg, collects premium)
///   Buy  OTM Put  400 pts below short leg  (long leg, caps max loss)
///
/// Automatic exits:
///   Profit target : 50% of net premium collected
///   Stop loss     : 2× net premium collected
///   Expiry day    : close at 14:45 IST regardless of P&amp;L
/// </summary>
public class BankNiftyBullPutEngine : BaseSpreadEngine
{
    // ── Strategy identity and parameters ─────────────────────────────────────

    public override string StrategyType              => "BankNiftyBullPutSpread";
    protected override string Underlying             => "BANKNIFTY";
    protected override string SpotSymbol             => "NIFTY BANK";
    protected override string SpotExchange           => "NSE";
    protected override string OptionsExchange        => "NFO";
    protected override int    SpotInstrumentToken    => 260105;
    protected override int    StrikeInterval         => 100;
    protected override int    SpreadWidth            => 400;
    protected override decimal VixThreshold          => 20m;
    protected override decimal AtrSpikeMultiple      => 1.5m;

    /// <summary>
    /// BANKNIFTY-specific VIX-adaptive multiplier. Adds a 0.2× base premium over
    /// the NIFTY formula to account for the index's structurally wider daily ranges.
    ///
    /// Formula : 1.2 + VIX / 20   (clamped to [1.50, 2.50])
    /// Examples : VIX 12 → 1.80×  |  VIX 15 → 1.95×  |  VIX 18 → 2.10×  |  VIX 20 → 2.20×
    /// </summary>
    protected override decimal ComputeAdrMultiplier(decimal vix)
    {
        var raw = 1.2m + (vix / 20m);
        return Math.Round(Math.Clamp(raw, 1.50m, 2.50m), 2);
    }
    protected override decimal ProfitTarget          => 0.50m;
    protected override decimal StopLossMultiple      => 2.00m;
    protected override DayOfWeek[] EntryDays         =>
        [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday];
    protected override TimeOnly ExpiryExitTime       => new(14, 45);

    // ── Constructor ───────────────────────────────────────────────────────────

    public BankNiftyBullPutEngine(
        IMarketDataProvider              marketData,
        IInstrumentRepository            instruments,
        IPositionRepository              positions,
        ILogger<BankNiftyBullPutEngine>  logger,
        IConfiguration                   configuration)
        : base(marketData, instruments, positions, logger, configuration)
    {
    }

    // ── Entry ─────────────────────────────────────────────────────────────────

    public override async Task<StrategySignal?> EvaluateAsync(
        StrategyExecution execution, CancellationToken ct = default)
    {
        var (passed, ctx) = await EvaluateEntryGatesAsync(
            execution,
            optionType:   OptionType.Put,
            spotAboveEma: true,   // Bull-Put requires spot > EMA20
            ct);

        if (!passed || ctx is null) return null;

        _logger.LogInformation(
            "[BNBPS] ENTRY signal — spot={S:F0} EMA={E:F0} | " +
            "Short {SS}PE@{SP:F2} Long {LS}PE@{LP:F2} | NetCredit={NC:F2} × {Q} = ₹{Total:F0}",
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
                    TradingSymbol:   $"{Underlying}{ctx.Expiry:yyMMdd}P{ctx.ShortStrike:F0}",
                    Exchange:        OptionsExchange,
                    InstrumentToken: 0,
                    Side:            OrderSide.Sell,
                    Quantity:        ctx.Quantity,
                    LastPrice:           ctx.ShortPremium,
                    OptionType:      OptionType.Put,
                    StrikePrice:     ctx.ShortStrike,
                    ExpiryDate:      ctx.Expiry),
                new(
                    TradingSymbol:   $"{Underlying}{ctx.Expiry:yyMMdd}P{ctx.LongStrike:F0}",
                    Exchange:        OptionsExchange,
                    InstrumentToken: 0,
                    Side:            OrderSide.Buy,
                    Quantity:        ctx.Quantity,
                    LastPrice:           ctx.LongPremium,
                    OptionType:      OptionType.Put,
                    StrikePrice:     ctx.LongStrike,
                    ExpiryDate:      ctx.Expiry)
            },
            Rationale:
                $"BANKNIFTY {ctx.Spot:F0} > EMA20 {ctx.Ema20:F0} | " +
                $"ADR {ctx.Adr:F0}pts → offset {ctx.AdrBasedOffset}pts | " +
                $"ATR {ctx.Atr:F0}pts (avg {ctx.AtrAverage:F0}pts) | " +
                $"Short {ctx.ShortStrike}PE @{ctx.ShortPremium:F2} | Long {ctx.LongStrike}PE @{ctx.LongPremium:F2} | " +
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

    // ── Exit ──────────────────────────────────────────────────────────────────

    public override Task<StrategySignal?> EvaluateExitAsync(
        StrategyExecution execution, CancellationToken ct = default)
        => EvaluateExitCoreAsync(execution, ct);

    // ── Expiry helper ─────────────────────────────────────────────────────────

    protected override DateOnly GetNearestExpiry(DateTime istNow)
        => NearestWednesdayExpiry(istNow);
}
