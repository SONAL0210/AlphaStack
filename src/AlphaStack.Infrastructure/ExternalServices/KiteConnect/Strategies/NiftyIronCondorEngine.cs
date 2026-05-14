using Microsoft.Extensions.Logging;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Domain.Entities;
using AlphaStack.Domain.Enums;
using Microsoft.Extensions.Configuration;
using AlphaStack.Infrastructure.BackgroundServices;

namespace AlphaStack.Infrastructure.Strategies;

/// <summary>
/// NIFTY Iron Condor signal engine.
///
/// An Iron Condor simultaneously sells an OTM Put spread (bull-put) AND an OTM
/// Call spread (bear-call) on the same expiry, profiting from a range-bound market
/// that stays between the two short strikes through expiry.
///
/// Entry conditions (all must be true):
///   1. Entry day is Wednesday only (gives ~6 days to Tuesday expiry)
///   2. No existing open position on this execution
///   3. India VIX in [14, 20) — range-bound regime, not too quiet, not too nervous
///   4. NIFTY spot within EMA20 ± 0.5 × ADR — neutral, no strong trend
///   5. ATR not spiking (< 1.5 × 20-day ATR average)
///   6. Today's gap < 1%
///   7. Each wing produces ≥ 1% of spread width in net credit
///
/// Legs (all on nearest weekly Tuesday expiry):
///   SELL  OTM Put   ~ADR×1.5 pts below spot  (put short leg)
///   BUY   OTM Put   200 pts below short put   (put long leg)
///   SELL  OTM Call  ~ADR×1.5 pts above spot   (call short leg)
///   BUY   OTM Call  200 pts above short call   (call long leg)
///
/// Automatic exits:
///   Profit target : 50% of combined net premium collected
///   Stop loss     : 2× combined net premium collected
///   Expiry day    : close all 4 legs at 14:45 IST
/// </summary>
public class NiftyIronCondorEngine : BaseSpreadEngine
{
    // ── Strategy identity and parameters ─────────────────────────────────────

    public override string StrategyType              => "NiftyIronCondor";
    protected override string Underlying             => "NIFTY";
    protected override string SpotSymbol             => "NIFTY 50";
    protected override string SpotExchange           => "NSE";
    protected override string OptionsExchange        => "NFO";
    protected override int    SpotInstrumentToken    => 256265;
    protected override int    StrikeInterval         => 50;
    protected override int    SpreadWidth            => 200;
    protected override decimal VixThreshold          => 20m;   // upper VIX bound (exclusive)
    protected override decimal AtrSpikeMultiple      => 1.5m;
    protected override decimal ProfitTarget          => 0.50m;
    protected override decimal StopLossMultiple      => 2.00m;
    protected override DayOfWeek[] EntryDays         => [DayOfWeek.Wednesday];
    protected override TimeOnly ExpiryExitTime       => new(14, 45);

    /// <summary>
    /// Lower VIX bound (inclusive). Below this the market is too complacent
    /// for a premium-selling strategy to collect meaningful credit.
    /// </summary>
    private const decimal VixFloor = 14m;

    // ── Constructor ───────────────────────────────────────────────────────────

    public NiftyIronCondorEngine(
        IMarketDataProvider             marketData,
        IInstrumentRepository           instruments,
        IPositionRepository             positions,
        ILogger<NiftyIronCondorEngine>  logger,
        IConfiguration                  configuration,
        IInstrumentSyncState syncState)
        : base(marketData, instruments, positions, logger, configuration,syncState)
    {
    }

    // ── Entry ─────────────────────────────────────────────────────────────────

    public override async Task<StrategySignal?> EvaluateAsync(
        StrategyExecution execution, CancellationToken ct = default)
    {
        var (passed, ctx) = await EvaluateIronCondorGatesAsync(execution, VixFloor, ct);
        if (!passed || ctx is null) return null;

        _logger.LogInformation(
            "[IC] ENTRY signal — spot={S:F0} EMA={E:F0} VIX={V:F1} | " +
            "Put {SP}PE@{SPP:F2}/{LP}PE@{LPP:F2} | Call {SC}CE@{SCP:F2}/{LC}CE@{LCP:F2} | " +
            "PutCredit={PC:F2} CallCredit={CC:F2} NetCredit={NC:F2} × {Q} = ₹{Total:F0}",
            ctx.Spot, ctx.Ema20, ctx.Vix,
            ctx.ShortPutStrike, ctx.ShortPutPremium, ctx.LongPutStrike, ctx.LongPutPremium,
            ctx.ShortCallStrike, ctx.ShortCallPremium, ctx.LongCallStrike, ctx.LongCallPremium,
            ctx.PutCredit, ctx.CallCredit, ctx.NetCredit,
            ctx.Quantity, ctx.NetCredit * ctx.Quantity);

        return new StrategySignal(
            SignalGroupId:       Guid.NewGuid(),
            StrategyExecutionId: execution.Id,
            StrategyType:        StrategyType,
            Action:              SignalAction.Enter,
            Mode:                execution.Mode,
            Legs: new List<SignalLeg>
            {
                // ── Bull-Put wing ──────────────────────────────────────────────
                new(
                    TradingSymbol:   $"NIFTY{ctx.Expiry:yyMMdd}P{ctx.ShortPutStrike:F0}",
                    Exchange:        OptionsExchange,
                    InstrumentToken: 0,
                    Side:            OrderSide.Sell,
                    Quantity:        ctx.Quantity,
                    LastPrice:           ctx.ShortPutPremium,
                    OptionType:      OptionType.Put,
                    StrikePrice:     ctx.ShortPutStrike,
                    ExpiryDate:      ctx.Expiry),
                new(
                    TradingSymbol:   $"NIFTY{ctx.Expiry:yyMMdd}P{ctx.LongPutStrike:F0}",
                    Exchange:        OptionsExchange,
                    InstrumentToken: 0,
                    Side:            OrderSide.Buy,
                    Quantity:        ctx.Quantity,
                    LastPrice:           ctx.LongPutPremium,
                    OptionType:      OptionType.Put,
                    StrikePrice:     ctx.LongPutStrike,
                    ExpiryDate:      ctx.Expiry),

                // ── Bear-Call wing ─────────────────────────────────────────────
                new(
                    TradingSymbol:   $"NIFTY{ctx.Expiry:yyMMdd}C{ctx.ShortCallStrike:F0}",
                    Exchange:        OptionsExchange,
                    InstrumentToken: 0,
                    Side:            OrderSide.Sell,
                    Quantity:        ctx.Quantity,
                    LastPrice:           ctx.ShortCallPremium,
                    OptionType:      OptionType.Call,
                    StrikePrice:     ctx.ShortCallStrike,
                    ExpiryDate:      ctx.Expiry),
                new(
                    TradingSymbol:   $"NIFTY{ctx.Expiry:yyMMdd}C{ctx.LongCallStrike:F0}",
                    Exchange:        OptionsExchange,
                    InstrumentToken: 0,
                    Side:            OrderSide.Buy,
                    Quantity:        ctx.Quantity,
                    LastPrice:           ctx.LongCallPremium,
                    OptionType:      OptionType.Call,
                    StrikePrice:     ctx.LongCallStrike,
                    ExpiryDate:      ctx.Expiry)
            },
            Rationale:
                $"NIFTY {ctx.Spot:F0} ≈ EMA20 {ctx.Ema20:F0} (neutral) | " +
                $"VIX {ctx.Vix:F1} in [{VixFloor}–{VixThreshold}) | " +
                $"ADR {ctx.Adr:F0}pts → offset {ctx.AdrBasedOffset}pts | " +
                $"Put wing: {ctx.ShortPutStrike}PE @{ctx.ShortPutPremium:F2} / {ctx.LongPutStrike}PE @{ctx.LongPutPremium:F2} | " +
                $"Call wing: {ctx.ShortCallStrike}CE @{ctx.ShortCallPremium:F2} / {ctx.LongCallStrike}CE @{ctx.LongCallPremium:F2} | " +
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

    /// <summary>
    /// Delegates to <see cref="BaseSpreadEngine.EvaluateExitCoreAsync"/>.
    /// The base exit core already handles N-leg groups correctly — it sums
    /// all sell-leg entry premiums minus all buy-leg entry premiums for the
    /// combined net credit, and sums UnrealizedPnL across all 4 legs.
    /// </summary>
    public override Task<StrategySignal?> EvaluateExitAsync(
        StrategyExecution execution, CancellationToken ct = default)
        => EvaluateExitCoreAsync(execution, ct);

    // ── Expiry helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Entered on Wednesday; targets the nearest Tuesday expiry (~6 days out).
    /// </summary>
    protected override DateOnly GetNearestExpiry(DateTime istNow)
    => NearestTuesdayExpiry(istNow); 
}
