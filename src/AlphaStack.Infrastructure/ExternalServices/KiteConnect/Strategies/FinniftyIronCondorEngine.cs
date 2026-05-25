using Microsoft.Extensions.Logging;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Domain.Entities;
using AlphaStack.Domain.Enums;
using Microsoft.Extensions.Configuration;
using AlphaStack.Infrastructure.BackgroundServices;

namespace AlphaStack.Infrastructure.Strategies;

/// <summary>
/// FINNIFTY Iron Condor signal engine.
/// Entry on Wednesday — targets nearest Tuesday expiry (6 days out).
/// Lot size: 40 | Strike interval: 50pts
/// </summary>
public class FinniftyIronCondorEngine : BaseSpreadEngine
{
    public override string StrategyType        => "FinniftyIronCondor";
    protected override string Underlying       => "FINNIFTY";
    protected override string SpotSymbol       => "NIFTY FIN SERVICE";
    protected override string SpotExchange     => "NSE";
    protected override string OptionsExchange  => "NSE";
    protected override int SpotInstrumentToken => 257801;
    protected override int StrikeInterval      => 50;
    protected override int SpreadWidth         => 200;
    protected override decimal VixThreshold    => 20m;
    protected override decimal AtrSpikeMultiple => 1.5m;
    protected override decimal ProfitTarget    => 0.50m;
    protected override decimal StopLossMultiple => 2.00m;
    protected override DayOfWeek[] EntryDays   => [DayOfWeek.Wednesday];
    protected override TimeOnly ExpiryExitTime => new(14, 45);

    private const decimal VixFloor = 14m;

    protected override decimal ComputeAdrMultiplier(decimal vix)
    {
        var raw = 1.0m + (vix / 20m);
        return Math.Round(Math.Clamp(raw, 1.25m, 2.25m), 2);
    }

    public FinniftyIronCondorEngine(
        IMarketDataProvider              marketData,
        IInstrumentRepository            instruments,
        IPositionRepository              positions,
        ILogger<FinniftyIronCondorEngine> logger,
        IConfiguration                   configuration,
        IInstrumentSyncState             syncState)
        : base(marketData, instruments, positions, logger, configuration, syncState) { }

    public override async Task<StrategySignal?> EvaluateAsync(
        StrategyExecution execution, CancellationToken ct = default)
    {
        var (passed, ctx) = await EvaluateIronCondorGatesAsync(execution, VixFloor, ct);
        if (!passed || ctx is null) return null;

        _logger.LogInformation(
            "[FNIC] ENTRY signal — spot={S:F0} EMA={E:F0} VIX={V:F1} | " +
            "Put {SP}PE@{SPP:F2}/{LP}PE@{LPP:F2} | Call {SC}CE@{SCP:F2}/{LC}CE@{LCP:F2} | " +
            "NetCredit={NC:F2} × {Q} = ₹{Total:F0}",
            ctx.Spot, ctx.Ema20, ctx.Vix,
            ctx.ShortPutStrike, ctx.ShortPutPremium, ctx.LongPutStrike, ctx.LongPutPremium,
            ctx.ShortCallStrike, ctx.ShortCallPremium, ctx.LongCallStrike, ctx.LongCallPremium,
            ctx.NetCredit, ctx.Quantity, ctx.NetCredit * ctx.Quantity);

        return new StrategySignal(
            SignalGroupId:       Guid.NewGuid(),
            StrategyExecutionId: execution.Id,
            StrategyType:        StrategyType,
            Action:              SignalAction.Enter,
            Mode:                execution.Mode,
            Legs: new List<SignalLeg>
            {
                new(TradingSymbol:   $"FINNIFTY{ctx.Expiry:yyMMdd}P{ctx.ShortPutStrike:F0}",
                    Exchange:        OptionsExchange, InstrumentToken: 0,
                    Side:            OrderSide.Sell,  Quantity: ctx.Quantity,
                    LastPrice:       ctx.ShortPutPremium, OptionType: OptionType.Put,
                    StrikePrice:     ctx.ShortPutStrike, ExpiryDate: ctx.Expiry),
                new(TradingSymbol:   $"FINNIFTY{ctx.Expiry:yyMMdd}P{ctx.LongPutStrike:F0}",
                    Exchange:        OptionsExchange, InstrumentToken: 0,
                    Side:            OrderSide.Buy,   Quantity: ctx.Quantity,
                    LastPrice:       ctx.LongPutPremium, OptionType: OptionType.Put,
                    StrikePrice:     ctx.LongPutStrike, ExpiryDate: ctx.Expiry),
                new(TradingSymbol:   $"FINNIFTY{ctx.Expiry:yyMMdd}C{ctx.ShortCallStrike:F0}",
                    Exchange:        OptionsExchange, InstrumentToken: 0,
                    Side:            OrderSide.Sell,  Quantity: ctx.Quantity,
                    LastPrice:       ctx.ShortCallPremium, OptionType: OptionType.Call,
                    StrikePrice:     ctx.ShortCallStrike, ExpiryDate: ctx.Expiry),
                new(TradingSymbol:   $"FINNIFTY{ctx.Expiry:yyMMdd}C{ctx.LongCallStrike:F0}",
                    Exchange:        OptionsExchange, InstrumentToken: 0,
                    Side:            OrderSide.Buy,   Quantity: ctx.Quantity,
                    LastPrice:       ctx.LongCallPremium, OptionType: OptionType.Call,
                    StrikePrice:     ctx.LongCallStrike, ExpiryDate: ctx.Expiry)
            },
            Rationale:
                $"FINNIFTY {ctx.Spot:F0} ≈ EMA20 {ctx.Ema20:F0} (neutral) | " +
                $"VIX {ctx.Vix:F1} in [{VixFloor}–{VixThreshold}) | " +
                $"Put wing: {ctx.ShortPutStrike}PE / {ctx.LongPutStrike}PE | " +
                $"Call wing: {ctx.ShortCallStrike}CE / {ctx.LongCallStrike}CE | " +
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
        => NearestTuesdayExpiry(istNow);
}