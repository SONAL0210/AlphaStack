using Microsoft.Extensions.Logging;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Domain.Entities;
using AlphaStack.Domain.Enums;
using Microsoft.Extensions.Configuration;
using AlphaStack.Infrastructure.BackgroundServices;

namespace AlphaStack.Infrastructure.Strategies;

/// <summary>
/// Bear Call Spread signal engine for FINNIFTY.
/// FINNIFTY expires every Tuesday (same as NIFTY).
/// Lot size: 40 | Strike interval: 50pts
/// </summary>
public class FinniftyBearCallEngine : BaseSpreadEngine
{
    public override string StrategyType        => "FinniftyBearCallSpread";
    protected override string Underlying       => "FINNIFTY";
    protected override string SpotSymbol       => "NIFTY FIN SERVICE";
    protected override string SpotExchange     => "NSE";
    protected override string OptionsExchange  => "NSE";
    protected override int SpotInstrumentToken => 257801;       // NSE:FINNIFTY-INDEX
    protected override int StrikeInterval      => 50;
    protected override int SpreadWidth         => 200;          // 4 strikes wide at 50pts
    protected override decimal VixThreshold    => 20m;
    protected override decimal AtrSpikeMultiple => 1.5m;
    protected override decimal ProfitTarget    => 0.50m;
    protected override decimal StopLossMultiple => 2.00m;
    protected override DayOfWeek[] EntryDays   =>
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
         DayOfWeek.Thursday, DayOfWeek.Friday];
    protected override TimeOnly ExpiryExitTime => new(14, 45);

    // FINNIFTY uses same base multiplier as NIFTY (1.0 + VIX/20)
    // No +0.2 premium — FINNIFTY vol profile closer to NIFTY than FINNIFTY
    protected override decimal ComputeAdrMultiplier(decimal vix)
    {
        var raw = 1.0m + (vix / 20m);
        return Math.Round(Math.Clamp(raw, 1.25m, 2.25m), 2);
    }

    public FinniftyBearCallEngine(
        IMarketDataProvider           marketData,
        IInstrumentRepository         instruments,
        IPositionRepository           positions,
        ILogger<FinniftyBearCallEngine> logger,
        IConfiguration                configuration,
        IInstrumentSyncState          syncState)
        : base(marketData, instruments, positions, logger, configuration, syncState) { }

    public override async Task<StrategySignal?> EvaluateAsync(
        StrategyExecution execution, CancellationToken ct = default)
    {
        var (passed, ctx) = await EvaluateEntryGatesAsync(
            execution,
            optionType:   OptionType.Call,
            spotAboveEma: false,
            ct);

        if (!passed || ctx is null) return null;

        _logger.LogInformation(
            "[FNBCS] ENTRY signal — spot={S:F0} EMA={E:F0} | " +
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
                new(TradingSymbol:   $"{Underlying}{ctx.Expiry:yyMMdd}C{ctx.ShortStrike:F0}",
                    Exchange:        OptionsExchange,
                    InstrumentToken: 0,
                    Side:            OrderSide.Sell,
                    Quantity:        ctx.Quantity,
                    LastPrice:       ctx.ShortPremium,
                    OptionType:      OptionType.Call,
                    StrikePrice:     ctx.ShortStrike,
                    ExpiryDate:      ctx.Expiry),
                new(TradingSymbol:   $"{Underlying}{ctx.Expiry:yyMMdd}C{ctx.LongStrike:F0}",
                    Exchange:        OptionsExchange,
                    InstrumentToken: 0,
                    Side:            OrderSide.Buy,
                    Quantity:        ctx.Quantity,
                    LastPrice:       ctx.LongPremium,
                    OptionType:      OptionType.Call,
                    StrikePrice:     ctx.LongStrike,
                    ExpiryDate:      ctx.Expiry)
            },
            Rationale:
                $"FINNIFTY {ctx.Spot:F0} < EMA20 {ctx.Ema20:F0} | " +
                $"ADR {ctx.Adr:F0}pts → offset {ctx.AdrBasedOffset}pts | " +
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

    public override Task<StrategySignal?> EvaluateExitAsync(
        StrategyExecution execution, CancellationToken ct = default)
        => EvaluateExitCoreAsync(execution, ct);

    protected override DateOnly GetNearestExpiry(DateTime istNow)
        => NearestTuesdayExpiry(istNow);  // FINNIFTY expires Tuesday
}