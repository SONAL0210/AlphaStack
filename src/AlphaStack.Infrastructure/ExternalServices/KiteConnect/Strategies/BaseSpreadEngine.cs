using Microsoft.Extensions.Logging;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Domain.Entities;
using AlphaStack.Domain.Enums;
using Microsoft.Extensions.Configuration;
using AlphaStack.Infrastructure.BackgroundServices;
using AlphaStack.Application.Features.Trading;
using static AlphaStack.Application.Features.Trading.ShadowTradeLoggerService;

namespace AlphaStack.Infrastructure.Strategies;

/// <summary>
/// Abstract base for credit-spread engines (Bull Put, Bear Call, and future variants).
/// Encapsulates all shared infrastructure: indicator computation, gate checks, strike
/// selection, instrument lookup, premium validation, and exit logic.
///
/// Subclasses provide only the strategy-specific parameters (via abstract properties)
/// and the direction-aware entry signal construction (via EvaluateAsync override).
/// </summary>
public abstract class BaseSpreadEngine : IStrategyEngine
{
    // ── Abstract identity ─────────────────────────────────────────────────────

    /// <summary>Strategy type tag written into every StrategySignal.</summary>
    public abstract string StrategyType { get; }

    /// <summary>Underlying index name, e.g. "NIFTY" or "FINNIFTY".</summary>
    protected abstract string Underlying { get; }

    /// <summary>Spot quote symbol, e.g. "NIFTY 50" or "NIFTY FIN SERVICE".</summary>
    protected abstract string SpotSymbol { get; }

    /// <summary>Exchange for the spot quote, e.g. "NSE".</summary>
    protected abstract string SpotExchange { get; }

    /// <summary>Options exchange, e.g. "NFO".</summary>
    protected abstract string OptionsExchange { get; }

    /// <summary>Fyers/Kite historical-data instrument token for the spot index.</summary>
    protected abstract int SpotInstrumentToken { get; }

    /// <summary>Strike rounding interval: 50 for NIFTY, 100 for FINNIFTY.</summary>
    protected abstract int StrikeInterval { get; }

    /// <summary>Distance between short and long strike, e.g. 200 for NIFTY, 400 for FINNIFTY.</summary>
    protected abstract int SpreadWidth { get; }

    protected abstract decimal VixThreshold { get; }
    protected abstract decimal AtrSpikeMultiple { get; }

    internal ShadowMarketContext? LastEvaluatedShadowContext { get; private set; }

    /// <summary>
    /// Computes a VIX-adaptive ADR multiplier. Scales linearly with VIX so that
    /// strikes are placed further OTM in higher-volatility regimes, reducing the
    /// risk of early breaches.
    ///
    /// Formula : 1.0 + VIX / 20   (clamped to [1.25, 2.25])
    /// Examples : VIX 12 → 1.60×  |  VIX 15 → 1.75×  |  VIX 18 → 1.90×  |  VIX 20 → 2.00×
    ///
    /// FINNIFTY subclasses may override this with a higher base to account for
    /// the index's structurally wider daily ranges.
    /// </summary>
    protected virtual decimal ComputeAdrMultiplier(decimal vix)
    {
        var raw = 1.0m + (vix / 20m);
        return Math.Round(Math.Clamp(raw, 1.25m, 2.25m), 2);
    }
    protected abstract decimal ProfitTarget { get; }
    protected abstract decimal StopLossMultiple { get; }

    /// <summary>Days of the week on which entry is permitted.</summary>
    protected abstract DayOfWeek[] EntryDays { get; }

    /// <summary>Time on expiry day after which positions are force-closed.</summary>
    protected abstract TimeOnly ExpiryExitTime { get; }

    // ── Shared infrastructure ─────────────────────────────────────────────────

    protected readonly IMarketDataProvider _marketData;
    protected readonly IInstrumentRepository _instruments;
    protected readonly IPositionRepository _positions;
    protected readonly ILogger _logger;
    private readonly IConfiguration _configuration;
    private readonly IInstrumentSyncState _syncState;

    private const string VixSymbol = "INDIA VIX";
    private const string VixExchange = "NSE";

    protected static readonly TimeZoneInfo Ist =
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    private bool IsEma50FilterEnabled =>
        _configuration.GetValue<bool>("StrategySettings:Ema50FilterEnabled", defaultValue: true);

    protected BaseSpreadEngine(
        IMarketDataProvider marketData,
        IInstrumentRepository instruments,
        IPositionRepository positions,
        ILogger logger,
        IConfiguration configuration,
        IInstrumentSyncState syncState)
    {
        _marketData = marketData;
        _instruments = instruments;
        _positions = positions;
        _logger = logger;
        _configuration = configuration;
        _syncState = syncState;
    }

    // ── Abstract entry / exit (implemented by each subclass) ─────────────────

    public abstract Task<StrategySignal?> EvaluateAsync(
        StrategyExecution execution, CancellationToken ct = default);

    public abstract Task<StrategySignal?> EvaluateExitAsync(
        StrategyExecution execution, CancellationToken ct = default);

    // ── Abstract expiry helper (subclass provides the correct weekday) ────────

    /// <summary>
    /// Returns the nearest upcoming expiry date for this underlying.
    /// e.g. NearestTuesdayExpiry for NIFTY, NearestWednesdayExpiry for FINNIFTY.
    /// </summary>
    protected abstract DateOnly GetNearestExpiry(DateTime istNow);

    // ── Shared record types ───────────────────────────────────────────────────

    private record MarketIndicators(
        decimal Ema20,
        decimal Ema50,
        decimal Adr,
        decimal Atr,
        decimal AtrAverage,
        decimal GapPercent);

    /// <summary>
    /// All market data needed to build the entry StrategySignal, computed and
    /// validated by <see cref="EvaluateEntryGatesAsync"/>. Subclasses receive this
    /// if every gate passes and construct their signal from it.
    /// </summary>
    protected record MarketContext(
        decimal Spot,
        decimal Vix,
        decimal Ema20,
        decimal Ema50,
        decimal Adr,
        decimal Atr,
        decimal AtrAverage,
        decimal GapPercent,
        decimal AdrMultiplierUsed,
        int AdrBasedOffset,
        decimal ShortStrike,
        decimal LongStrike,
        DateOnly Expiry,
        decimal ShortPremium,
        decimal LongPremium,
        decimal NetCredit,
        int Quantity);

    // ── Shared entry gate ─────────────────────────────────────────────────────

    /// <summary>
    /// Runs all shared entry gates in sequence and returns a fully populated
    /// <see cref="MarketContext"/> when every gate passes, or <c>(false, null)</c>
    /// on the first failure.
    ///
    /// <paramref name="optionType"/>   — Put for Bull-Put, Call for Bear-Call.
    /// <paramref name="spotAboveEma"/> — true  = Bull-Put (requires spot &gt; EMA20);
    ///                                   false = Bear-Call (requires spot &lt; EMA20).
    /// </summary>
    protected async Task<(bool Passed, MarketContext? Context)> EvaluateEntryGatesAsync(
    StrategyExecution execution,
    OptionType optionType,
    bool spotAboveEma,
    CancellationToken ct)
    {
        LastEvaluatedShadowContext = null;

        // Gate 1: entry day
        var istNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Ist);
        if (!EntryDays.Contains(istNow.DayOfWeek))
        {
            _logger.LogInformation(
                "[{Type}] Skip — {Day} is not an entry day ({Days})",
                StrategyType, istNow.DayOfWeek, string.Join("/", EntryDays));
            return (false, null);
        }

        // Gate 1.5: synthetic instrument guard
        if (_syncState.LastSyncWasSynthetic)
        {
            _logger.LogWarning(
                "[{Type}] Skip — last instrument sync used synthetic data.",
                StrategyType);
            return (false, null);
        }

        // ── Fetch market data early so shadow context is always set ──────────
        // Done before Gate 2 so blocked evaluations still produce shadow variants.
        // This costs 3 API calls per blocked strategy but gives shadow data on
        // days where a real position is already open — most valuable for comparison.

        var vixQuote = await FetchQuoteSafeAsync(VixSymbol, VixExchange, ct);
        if (vixQuote is null) return (false, null);

        var spotQuote = await FetchQuoteSafeAsync(SpotSymbol, SpotExchange, ct);
        if (spotQuote is null) return (false, null);

        var indicators = await ComputeIndicatorsAsync(ct);
        if (indicators is null) return (false, null);

        var vixRegime = vixQuote.LastPrice < 14 ? "VIX_LOW"
            : vixQuote.LastPrice < 18 ? "VIX_MID" : "VIX_HIGH";

        var regimeValid = spotAboveEma
            ? spotQuote.LastPrice > indicators.Ema20
            : spotQuote.LastPrice < indicators.Ema20;

        LastEvaluatedShadowContext = new ShadowMarketContext(
            Spot:               spotQuote.LastPrice,
            Vix:                vixQuote.LastPrice,
            VixRegime:          vixRegime,
            Ema20:              indicators.Ema20,
            Adr:                indicators.Adr,
            Atr:                indicators.Atr,
            AtrAverage:         indicators.AtrAverage,
            GapPercent:         indicators.GapPercent,
            DaysToExpiry:       (GetNearestExpiry(istNow).ToDateTime(TimeOnly.MinValue)
                                - istNow.Date).Days,
            Expiry:             GetNearestExpiry(istNow),
            RealNetCredit:      0m,
            MarketRegimeValid:  regimeValid);  // filled in by subclass if signal fires

        // Gate 2: no existing open position on this execution
        var open = await _positions.GetOpenByExecutionAsync(execution.Id, ct);
        if (open.Any())
        {
            _logger.LogInformation(
                "[{Type}] Skip — {Count} open position(s) already exist",
                StrategyType, open.Count);

            LastEvaluatedShadowContext = LastEvaluatedShadowContext with
            {
                WasPositionBlocked = true
            };
            return (false, null);  // shadow context is set — logger will fire
        }

        // Gate 3: VIX below threshold
        if (vixQuote.LastPrice >= VixThreshold)
        {
            _logger.LogInformation(
                "[{Type}] Skip — VIX {V:F1} >= {T}",
                StrategyType, vixQuote.LastPrice, VixThreshold);
            return (false, null);
        }
        
        var emaConditionMet = spotAboveEma
            ? spotQuote.LastPrice > indicators.Ema20
            : spotQuote.LastPrice < indicators.Ema20;

        if (!emaConditionMet)
        {
            var direction = spotAboveEma ? "below" : "above";
            _logger.LogInformation(
                "[{Type}] Skip — spot {S:F0} is {Dir} EMA20 {E:F0}",
                StrategyType, spotQuote.LastPrice, direction, indicators.Ema20);
            return (false, null);
        }

        // Gate 6b: EMA50 trend confirmation (configurable)
        if (IsEma50FilterEnabled)
        {
            if (spotAboveEma && spotQuote.LastPrice < indicators.Ema50)
            {
                _logger.LogInformation(
                    "[{Type}] Skip — spot {S:F0} below EMA50 {E50:F0} (bearish trend, no BullPut)",
                    StrategyType, spotQuote.LastPrice, indicators.Ema50);
                return (false, null);
            }

            if (!spotAboveEma && spotQuote.LastPrice > indicators.Ema50)
            {
                _logger.LogInformation(
                    "[{Type}] Skip — spot {S:F0} above EMA50 {E50:F0} (bullish trend, no BearCall)",
                    StrategyType, spotQuote.LastPrice, indicators.Ema50);
                return (false, null);
            }
        }

        // Gate 7: ATR spike filter
        if (indicators.Atr > indicators.AtrAverage * AtrSpikeMultiple)
        {
            _logger.LogInformation(
                "[{Type}] Skip — ATR spike {A:F0} > {M}× avg {AA:F0}",
                StrategyType, indicators.Atr, AtrSpikeMultiple, indicators.AtrAverage);
            return (false, null);
        }

        // Gate 8: gap filter
        if (Math.Abs(indicators.GapPercent) > 1.0m)
        {
            _logger.LogInformation(
                "[{Type}] Skip — gap {G:F2}% exceeds 1% filter",
                StrategyType, indicators.GapPercent);
            return (false, null);
        }

        // Strike selection
        var multiplier = ComputeAdrMultiplier(vixQuote.LastPrice);
        var adrBasedOffset = Math.Max(
            SpreadWidth,
            (int)Math.Round(indicators.Adr * multiplier / StrikeInterval) * StrikeInterval);

        decimal shortStrike, longStrike;
        if (spotAboveEma)
        {
            shortStrike = RoundToStrike(spotQuote.LastPrice - adrBasedOffset);
            longStrike  = shortStrike - SpreadWidth;
        }
        else
        {
            shortStrike = RoundToStrike(spotQuote.LastPrice + adrBasedOffset);
            longStrike  = shortStrike + SpreadWidth;
        }

        _logger.LogInformation(
            "[{Type}] VIX={Vix:F1} → AdrMultiplier={Mult}x | ADR={A:F0}pts offset={O}pts short={S} long={L}",
            StrategyType, vixQuote.LastPrice, multiplier, indicators.Adr, adrBasedOffset, shortStrike, longStrike);

        // Gate 9: instrument lookup
        var expiry    = GetNearestExpiry(istNow);
        var shortInst = await FindOptionAsync(Underlying, expiry, optionType, shortStrike, ct);
        var longInst  = await FindOptionAsync(Underlying, expiry, optionType, longStrike, ct);

        if (shortInst is null || longInst is null)
        {
            _logger.LogWarning(
                "[{Type}] Instruments not found — {SS}/{LS} {Exp}. Run instrument sync first.",
                StrategyType, shortStrike, longStrike, expiry);
            return (false, null);
        }

        // Gate 10: live option premiums
        var shortQ = await FetchQuoteSafeAsync(shortInst.TradingSymbol, OptionsExchange, ct);
        var longQ  = await FetchQuoteSafeAsync(longInst.TradingSymbol, OptionsExchange, ct);
        if (shortQ is null || longQ is null) return (false, null);

        // Gate 11: minimum net credit
        var netCredit = shortQ.LastPrice - longQ.LastPrice;
        var minCredit = SpreadWidth * 0.01m;
        if (netCredit < minCredit)
        {
            _logger.LogInformation(
                "[{Type}] Skip — net credit ₹{C:F2} below minimum ₹{M:F2}",
                StrategyType, netCredit, minCredit);
            return (false, null);
        }

        var context = new MarketContext(
            Spot:              spotQuote.LastPrice,
            Vix:               vixQuote.LastPrice,
            Ema20:             indicators.Ema20,
            Ema50:             indicators.Ema50,
            Adr:               indicators.Adr,
            Atr:               indicators.Atr,
            AtrAverage:        indicators.AtrAverage,
            GapPercent:        indicators.GapPercent,
            AdrMultiplierUsed: multiplier,
            AdrBasedOffset:    adrBasedOffset,
            ShortStrike:       shortStrike,
            LongStrike:        longStrike,
            Expiry:            expiry,
            ShortPremium:      shortQ.LastPrice,
            LongPremium:       longQ.LastPrice,
            NetCredit:         netCredit,
            Quantity:          (int)shortInst.LotSize);

        return (true, context);
    }

    // ── Shared exit core ──────────────────────────────────────────────────────

    /// <summary>
    /// Shared exit evaluation. Each engine's EvaluateExitAsync delegates here.
    /// Checks profit target, stop loss, and expiry-day close for every open group.
    /// </summary>
    protected async Task<StrategySignal?> EvaluateExitCoreAsync(
        StrategyExecution execution, CancellationToken ct)
    {
        var open = await _positions.GetOpenByExecutionAsync(execution.Id, ct);
        if (!open.Any()) return null;

        var istNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Ist);
        var groups = open.GroupBy(p => p.SignalGroupId);

        // Fetch spot once for analytics (SpotAtExit)
        var spotQuote = await FetchQuoteSafeAsync(SpotSymbol, SpotExchange, ct);
        var spotAtExit = spotQuote?.LastPrice ?? 0m;

        foreach (var group in groups)
        {
            var legs = group.ToList();

            // Refresh current prices for all legs
            foreach (var leg in legs)
            {
                var q = await FetchQuoteSafeAsync(leg.TradingSymbol, leg.Exchange, ct);
                if (q is not null) leg.UpdateCurrentPrice(q.LastPrice);
            }

            var sellLegs = legs.Where(p => p.Side == OrderSide.Sell).ToList();
            var buyLegs = legs.Where(p => p.Side == OrderSide.Buy).ToList();
            if (sellLegs.Count == 0 || buyLegs.Count == 0) continue;

            // Works for both 2-leg spreads and 4-leg iron condors:
            // entryCredit = Σ(sell entry premiums) - Σ(buy entry premiums), × lot size
            var lotSize = sellLegs[0].Quantity;
            var entryCredit = (sellLegs.Sum(p => p.EntryPrice) - buyLegs.Sum(p => p.EntryPrice)) * lotSize;
            var currentPnL = legs.Sum(p => p.UnrealizedPnL);

            // Use any short leg for expiry check (all legs share the same expiry)
            var shortLeg = sellLegs[0];

            // 1. Profit target
            if (currentPnL >= entryCredit * ProfitTarget)
            {
                _logger.LogInformation(
                    "[{Type}] EXIT profit target — PnL ₹{PnL:F0} >= target ₹{T:F0}",
                    StrategyType, currentPnL, entryCredit * ProfitTarget);
                return BuildExitSignal(execution, group.Key, legs, "Profit target (50%) hit", spotAtExit);
            }

            // 2. Stop loss
            if (currentPnL <= -(entryCredit * StopLossMultiple))
            {
                _logger.LogInformation(
                    "[{Type}] EXIT stop loss — PnL ₹{PnL:F0} <= SL ₹{SL:F0}",
                    StrategyType, currentPnL, -(entryCredit * StopLossMultiple));
                return BuildExitSignal(execution, group.Key, legs, "Stop loss (2×) triggered", spotAtExit);
            }

            // 3. Expiry-day close. Also flattens stale expired positions from later tracker cycles.
            if (ShouldExitForExpiry(shortLeg.ExpiryDate, istNow))
            {
                _logger.LogInformation(
                    "[{Type}] EXIT expiry-day close — Expiry={Expiry} Time={Time}",
                    StrategyType, shortLeg.ExpiryDate, TimeOnly.FromDateTime(istNow));
                return BuildExitSignal(execution, group.Key, legs, "Expiry day close at 14:45", spotAtExit);
            }
        }

        return null;
    }

    // ── Protected helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Constructs a reversing exit signal. Sell legs become Buy orders and vice-versa.
    /// </summary>
    protected StrategySignal BuildExitSignal(
        StrategyExecution execution,
        Guid signalGroupId,
        List<Position> legs,
        string reason,
        decimal spotAtExit = 0m)
    {
        return new StrategySignal(
            SignalGroupId: signalGroupId,
            StrategyExecutionId: execution.Id,
            StrategyType: StrategyType,
            Action: SignalAction.Exit,
            Mode: execution.Mode,
            Legs: legs.Select(p => new SignalLeg(
                p.TradingSymbol, p.Exchange, p.InstrumentToken,
                // Reverse side to close: sold → buy back, bought → sell
                p.Side == OrderSide.Sell ? OrderSide.Buy : OrderSide.Sell,
                p.Quantity, p.CurrentPrice,
                p.OptionType, p.StrikePrice, p.ExpiryDate)).ToList(),
            Rationale: reason,
            GeneratedAt: DateTime.UtcNow,
            SpotAtSignal: spotAtExit);
    }

    /// <summary>Rounds a raw price to the nearest valid strike for this underlying.</summary>
    protected decimal RoundToStrike(decimal price)
        => Math.Round(price / StrikeInterval) * StrikeInterval;

    /// <summary>
    /// Returns true when the position must be closed due to expiry:
    /// either the expiry date is in the past, or today is expiry day and the
    /// configured exit time has been reached.
    /// </summary>
    protected bool ShouldExitForExpiry(DateOnly? expiryDate, DateTime istNow)
    {
        if (!expiryDate.HasValue) return false;

        var today = DateOnly.FromDateTime(istNow);
        if (expiryDate.Value < today) return true;
        if (expiryDate.Value > today) return false;

        return TimeOnly.FromDateTime(istNow) >= ExpiryExitTime;
    }

    /// <summary>Fetches a live quote; returns null and logs a warning on any failure.</summary>
    protected async Task<Quote?> FetchQuoteSafeAsync(
        string symbol, string exchange, CancellationToken ct)
    {
        try
        {
            return await _marketData.GetQuoteAsync(symbol, exchange, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Type}] Failed to fetch quote for {Symbol}", StrategyType, symbol);
            return null;
        }
    }

    // ── Static calculation helpers (internal so unit tests can call them) ─────

    /// <summary>Standard EMA. Seeds with the SMA of the first <paramref name="period"/> closes.</summary>
    internal static decimal CalculateEma(List<decimal> closes, int period)
    {
        if (closes.Count < period) throw new ArgumentException("Not enough data for EMA.");
        var k = 2m / (period + 1);
        var ema = closes.Take(period).Average();
        foreach (var c in closes.Skip(period))
            ema = c * k + ema * (1 - k);
        return Math.Round(ema, 2);
    }

    internal static decimal CalculateAdr(IReadOnlyList<Candle> candles, int period = 20)
        => candles.TakeLast(period).Average(c => c.High - c.Low);

    internal static (decimal Atr, decimal AtrAverage) CalculateAtr(
        IReadOnlyList<Candle> candles, int period = 14)
    {
        var trueRanges = new List<decimal>();
        for (int i = 1; i < candles.Count; i++)
        {
            var curr = candles[i];
            var prev = candles[i - 1];
            var tr = Math.Max(
                curr.High - curr.Low,
                Math.Max(
                    Math.Abs(curr.High - prev.Close),
                    Math.Abs(curr.Low - prev.Close)));
            trueRanges.Add(tr);
        }

        return (
            Math.Round(trueRanges.TakeLast(period).Average(), 2),
            Math.Round(trueRanges.TakeLast(20).Average(), 2));
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<MarketIndicators?> ComputeIndicatorsAsync(CancellationToken ct)
    {
        try
        {
            var candles = await _marketData.GetHistoricalDataAsync(
                SpotInstrumentToken, "day",
                DateTime.UtcNow.AddDays(-120), DateTime.UtcNow, ct);

            if (candles.Count < 60)
            {
                _logger.LogWarning(
                    "[{Type}] Not enough candles ({Count}) for indicators — need at least 60 for EMA50",
                    StrategyType, candles.Count);
                return null;
            }

            var closes = candles.Select(c => c.Close).ToList();
            var ema20 = CalculateEma(closes, 20);
            var ema50 = CalculateEma(closes, 50);
            var adr = CalculateAdr(candles, 20);
            var (atr, atrAvg) = CalculateAtr(candles, 14);

            // Gap = today's open vs yesterday's close
            var previousClose = candles[^2].Close;
            var todayOpen = candles[^1].Open;
            var gapPercent = previousClose > 0
                ? Math.Round((todayOpen - previousClose) / previousClose * 100, 4)
                : 0m;

            _logger.LogInformation(
                "[{Type}] Indicators — EMA20={E20:F0} EMA50={E50:F0} ADR={Adr:F0}pts ATR={Atr:F0}pts ATRAvg={AtrAvg:F0}pts Gap={Gap:F2}%",
                StrategyType, ema20, ema50, adr, atr, atrAvg, gapPercent);

            return new MarketIndicators(ema20, ema50, adr, atr, atrAvg, gapPercent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Type}] Failed to compute indicators", StrategyType);
            return null;
        }
    }

    private async Task<Instrument?> FindOptionAsync(
        string underlying, DateOnly expiry, OptionType optionType, decimal strike, CancellationToken ct)
    {
        var candidates = await _instruments.FindOptionsAsync(underlying, expiry, optionType, ct);
        return candidates.FirstOrDefault(i => i.StrikePrice == strike);
    }

    // ── Iron Condor shared types ──────────────────────────────────────────────

    /// <summary>
    /// Fully evaluated 4-leg market context returned by
    /// <see cref="EvaluateIronCondorGatesAsync"/>. Contains both the put-spread
    /// and call-spread legs plus their combined net credit.
    /// </summary>
    protected record IronCondorContext(
        decimal Spot,
        decimal Vix,
        decimal Ema20,
        decimal Ema50,
        decimal Adr,
        decimal Atr,
        decimal AtrAverage,
        decimal GapPercent,
        int AdrBasedOffset,
        // Put spread (bull-put side)
        decimal ShortPutStrike,
        decimal LongPutStrike,
        decimal ShortPutPremium,
        decimal LongPutPremium,
        // Call spread (bear-call side)
        decimal ShortCallStrike,
        decimal LongCallStrike,
        decimal ShortCallPremium,
        decimal LongCallPremium,
        // Combined
        decimal PutCredit,
        decimal CallCredit,
        decimal NetCredit,
        DateOnly Expiry,
        int Quantity);

    /// <summary>
    /// Runs all Iron Condor entry gates and, if every gate passes, returns a fully
    /// populated <see cref="IronCondorContext"/>. Returns <c>(false, null)</c> on
    /// the first failure.
    ///
    /// Iron Condor-specific gates (in addition to the shared gates):
    ///   • VIX must be in range [VixFloor, VixThreshold) — range-bound regime
    ///   • Spot must be within EMA20 ± 0.5 × ADR — neutral market
    ///   • Both put-spread AND call-spread must clear minimum credit
    /// </summary>
    protected async Task<(bool Passed, IronCondorContext? Context)> EvaluateIronCondorGatesAsync(
        StrategyExecution execution,
        decimal vixFloor,
        CancellationToken ct)
    {
        if (StrategyType.StartsWith("Finnifty", StringComparison.OrdinalIgnoreCase) &&
            !_configuration.GetValue<bool>("Strategies:FinniftyEnabled", true))
        {
            _logger.LogInformation("[{S}] Disabled via config.", StrategyType);
            return (false, null);
        }
        LastEvaluatedShadowContext = null;

        // Gate 1: entry day
        var istNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Ist);
        if (!EntryDays.Contains(istNow.DayOfWeek))
        {
            _logger.LogInformation(
                "[{Type}] Skip — {Day} is not an entry day ({Days})",
                StrategyType, istNow.DayOfWeek, string.Join("/", EntryDays));
            return (false, null);
        }
        // AFTER — market data fetched before Gate 2, context always set
        // Fetch market data early — same pattern as EvaluateEntryGatesAsync
        // so shadow context is set even when position is blocked
        var vixQuote = await FetchQuoteSafeAsync(VixSymbol, VixExchange, ct);
        if (vixQuote is null) return (false, null);

        var spotQuote = await FetchQuoteSafeAsync(SpotSymbol, SpotExchange, ct);
        if (spotQuote is null) return (false, null);

        var indicators = await ComputeIndicatorsAsync(ct);
        if (indicators is null) return (false, null);

        var vixRegime   = vixQuote.LastPrice < 14 ? "VIX_LOW"
            : vixQuote.LastPrice < 18 ? "VIX_MID" : "VIX_HIGH";

        var neutralBand = indicators.Adr * 0.5m;
        var lowerBound  = indicators.Ema20 - neutralBand;
        var upperBound  = indicators.Ema20 + neutralBand;
        var regimeValid = spotQuote.LastPrice >= lowerBound
                    && spotQuote.LastPrice <= upperBound;

        LastEvaluatedShadowContext = new ShadowMarketContext(
            Spot:               spotQuote.LastPrice,
            Vix:                vixQuote.LastPrice,
            VixRegime:          vixRegime,
            Ema20:              indicators.Ema20,
            Adr:                indicators.Adr,
            Atr:                indicators.Atr,
            AtrAverage:         indicators.AtrAverage,
            GapPercent:         indicators.GapPercent,
            DaysToExpiry:       (GetNearestExpiry(istNow).ToDateTime(TimeOnly.MinValue)
                                - istNow.Date).Days,
            Expiry:             GetNearestExpiry(istNow),
            RealNetCredit:      0m,
            MarketRegimeValid:  regimeValid);

        // Gate 2: no existing open position
        var open = await _positions.GetOpenByExecutionAsync(execution.Id, ct);
        if (open.Any())
        {
            _logger.LogInformation(
                "[{Type}] Skip — {Count} open position(s) already exist",
                StrategyType, open.Count);
            LastEvaluatedShadowContext = LastEvaluatedShadowContext with
            {
                WasPositionBlocked = true
            };
            return (false, null);  // context set — StrategyRunnerService will log
        }

        // Gate 3: VIX in range-bound window [vixFloor, VixThreshold)
        if (vixQuote.LastPrice < vixFloor || vixQuote.LastPrice >= VixThreshold)
        {
            _logger.LogInformation(
                "[{Type}] Skip — VIX {V:F1} outside range-bound window [{F}–{T})",
                StrategyType, vixQuote.LastPrice, vixFloor, VixThreshold);
            return (false, null);
        }

        // Gate 4: spot neutral — lowerBound/upperBound already computed above
        if (spotQuote.LastPrice < lowerBound || spotQuote.LastPrice > upperBound)
        {
            _logger.LogInformation(
                "[{Type}] Skip — spot {S:F0} outside neutral band [{L:F0}–{U:F0}] (EMA20={E:F0} ± 0.5×ADR {A:F0}pts)",
                StrategyType, spotQuote.LastPrice, lowerBound, upperBound, indicators.Ema20, indicators.Adr);
            return (false, null);
        }

        // Gate 6: spot neutral — must be within EMA20 ± 0.5 × ADR
        if (spotQuote.LastPrice < lowerBound || spotQuote.LastPrice > upperBound)
        {
            _logger.LogInformation(
                "[{Type}] Skip — spot {S:F0} outside neutral band [{L:F0}–{U:F0}] (EMA20={E:F0} ± 0.5×ADR {A:F0}pts)",
                StrategyType, spotQuote.LastPrice, lowerBound, upperBound, indicators.Ema20, indicators.Adr);
            return (false, null);
        }

        // Gate 6b: EMA50 regime check for Iron Condor (configurable)
        // Spot should sit between EMA20 and EMA50 (within 1% tolerance) — the market is
        // transitioning / range-bound rather than trending strongly in either direction.
        if (IsEma50FilterEnabled)
        {
            var emaLow = Math.Min(indicators.Ema20, indicators.Ema50);
            var emaHigh = Math.Max(indicators.Ema20, indicators.Ema50) * 1.01m;
            var withinEmaBand = spotQuote.LastPrice >= emaLow && spotQuote.LastPrice <= emaHigh;
            if (!withinEmaBand)
            {
                _logger.LogInformation(
                    "[{Type}] Skip — spot {S:F0} not between EMA20 {E20:F0} and EMA50 {E50:F0} (trend too strong for Iron Condor)",
                    StrategyType, spotQuote.LastPrice, indicators.Ema20, indicators.Ema50);
                return (false, null);
            }
        }

        // Gate 7: ATR spike
        if (indicators.Atr > indicators.AtrAverage * AtrSpikeMultiple)
        {
            _logger.LogInformation(
                "[{Type}] Skip — ATR spike {A:F0} > {M}× avg {AA:F0}",
                StrategyType, indicators.Atr, AtrSpikeMultiple, indicators.AtrAverage);
            return (false, null);
        }

        // Gate 8: gap filter
        if (Math.Abs(indicators.GapPercent) > 1.0m)
        {
            _logger.LogInformation(
                "[{Type}] Skip — gap {G:F2}% exceeds 1% filter",
                StrategyType, indicators.GapPercent);
            return (false, null);
        }

        // Strike selection — symmetric around spot, VIX-adaptive multiplier
        var multiplier = ComputeAdrMultiplier(vixQuote.LastPrice);
        var adrBasedOffset = Math.Max(
            SpreadWidth,
            (int)Math.Round(indicators.Adr * multiplier / StrikeInterval) * StrikeInterval);

        var shortPutStrike = RoundToStrike(spotQuote.LastPrice - adrBasedOffset);
        var longPutStrike = shortPutStrike - SpreadWidth;
        var shortCallStrike = RoundToStrike(spotQuote.LastPrice + adrBasedOffset);
        var longCallStrike = shortCallStrike + SpreadWidth;

        _logger.LogInformation(
            "[{Type}] VIX={Vix:F1} → AdrMultiplier={Mult}x | Put: {SP}/{LP} | Call: {SC}/{LC} | offset={O}pts",
            StrategyType, vixQuote.LastPrice, multiplier,
            shortPutStrike, longPutStrike, shortCallStrike, longCallStrike, adrBasedOffset);

        // Gate 9: instrument lookup (all 4 legs)
        var expiry = GetNearestExpiry(istNow);
        var shortPutInst = await FindOptionAsync(Underlying, expiry, OptionType.Put, shortPutStrike, ct);
        var longPutInst = await FindOptionAsync(Underlying, expiry, OptionType.Put, longPutStrike, ct);
        var shortCallInst = await FindOptionAsync(Underlying, expiry, OptionType.Call, shortCallStrike, ct);
        var longCallInst = await FindOptionAsync(Underlying, expiry, OptionType.Call, longCallStrike, ct);

        if (shortPutInst is null || longPutInst is null || shortCallInst is null || longCallInst is null)
        {
            _logger.LogWarning(
                "[{Type}] Instruments not found — Put {SP}/{LP} Call {SC}/{LC} {Exp}. Run instrument sync.",
                StrategyType, shortPutStrike, longPutStrike, shortCallStrike, longCallStrike, expiry);
            return (false, null);
        }

        // Gate 10: live premiums for all 4 legs
        var shortPutQ = await FetchQuoteSafeAsync(shortPutInst.TradingSymbol, OptionsExchange, ct);
        var longPutQ = await FetchQuoteSafeAsync(longPutInst.TradingSymbol, OptionsExchange, ct);
        var shortCallQ = await FetchQuoteSafeAsync(shortCallInst.TradingSymbol, OptionsExchange, ct);
        var longCallQ = await FetchQuoteSafeAsync(longCallInst.TradingSymbol, OptionsExchange, ct);

        if (shortPutQ is null || longPutQ is null || shortCallQ is null || longCallQ is null)
            return (false, null);

        // Gate 11: minimum credit on each wing (1% of spread width) + combined minimum
        var putCredit = shortPutQ.LastPrice - longPutQ.LastPrice;
        var callCredit = shortCallQ.LastPrice - longCallQ.LastPrice;
        var netCredit = putCredit + callCredit;
        var minWingCredit = SpreadWidth * 0.01m;

        if (putCredit < minWingCredit)
        {
            _logger.LogInformation(
                "[{Type}] Skip — put wing credit ₹{C:F2} below minimum ₹{M:F2}",
                StrategyType, putCredit, minWingCredit);
            return (false, null);
        }

        if (callCredit < minWingCredit)
        {
            _logger.LogInformation(
                "[{Type}] Skip — call wing credit ₹{C:F2} below minimum ₹{M:F2}",
                StrategyType, callCredit, minWingCredit);
            return (false, null);
        }

        var context = new IronCondorContext(
            Spot: spotQuote.LastPrice,
            Vix: vixQuote.LastPrice,
            Ema20: indicators.Ema20,
            Ema50: indicators.Ema50,
            Adr: indicators.Adr,
            Atr: indicators.Atr,
            AtrAverage: indicators.AtrAverage,
            GapPercent: indicators.GapPercent,
            AdrBasedOffset: adrBasedOffset,
            ShortPutStrike: shortPutStrike,
            LongPutStrike: longPutStrike,
            ShortPutPremium: shortPutQ.LastPrice,
            LongPutPremium: longPutQ.LastPrice,
            ShortCallStrike: shortCallStrike,
            LongCallStrike: longCallStrike,
            ShortCallPremium: shortCallQ.LastPrice,
            LongCallPremium: longCallQ.LastPrice,
            PutCredit: putCredit,
            CallCredit: callCredit,
            NetCredit: netCredit,
            Expiry: expiry,
            Quantity: (int)shortPutInst.LotSize);

        return (true, context);
    }
    
    // ── Holiday-aware expiry resolution ──────────────────────────────────────

/// <summary>
/// NSE market holidays (trading holidays only, not settlement holidays).
/// Update this list at the start of each calendar year.
/// Source: https://www.nseindia.com/products-services/equity-market-timings-holidays
/// </summary>
private static readonly HashSet<DateOnly> NseHolidays = new()
{
    // 2025
    new DateOnly(2025, 1, 26),   // Republic Day
    new DateOnly(2025, 2, 26),   // Mahashivratri
    new DateOnly(2025, 3, 14),   // Holi
    new DateOnly(2025, 3, 31),   // Id-Ul-Fitr (Ramzan Eid)
    new DateOnly(2025, 4, 10),   // Shri Mahavir Jayanti
    new DateOnly(2025, 4, 14),   // Dr. Baba Saheb Ambedkar Jayanti
    new DateOnly(2025, 4, 18),   // Good Friday
    new DateOnly(2025, 5, 1),    // Maharashtra Day
    new DateOnly(2025, 8, 15),   // Independence Day
    new DateOnly(2025, 8, 27),   // Ganesh Chaturthi
    new DateOnly(2025, 10, 2),   // Mahatma Gandhi Jayanti
    new DateOnly(2025, 10, 2),   // Dussehra
    new DateOnly(2025, 10, 20),  // Diwali Laxmi Pujan (Muhurat trading may differ)
    new DateOnly(2025, 10, 21),  // Diwali Balipratipada
    new DateOnly(2025, 11, 5),   // Prakash Gurpurb Sri Guru Nanak Dev Ji
    new DateOnly(2025, 12, 25),  // Christmas

    // 2026 — update when NSE publishes the official list
    new DateOnly(2026, 1, 26),   // Republic Day
    new DateOnly(2026, 3, 20),   // Holi (tentative)
    new DateOnly(2026, 4, 3),    // Good Friday (tentative)
    new DateOnly(2026, 8, 15),   // Independence Day
    new DateOnly(2026, 10, 2),   // Mahatma Gandhi Jayanti
    new DateOnly(2026, 12, 25),  // Christmas
};

/// <summary>
/// Returns true if the given date is a weekend or NSE trading holiday.
/// </summary>
private static bool IsNonTradingDay(DateOnly date)
    => date.DayOfWeek == DayOfWeek.Saturday
    || date.DayOfWeek == DayOfWeek.Sunday
    || NseHolidays.Contains(date);

/// <summary>
/// Given a computed expiry date, checks if that date is a holiday or weekend.
/// If so, rolls BACKWARD to the nearest prior trading day (NSE convention —
/// expiry moves to the previous trading day when the scheduled day is a holiday).
/// </summary>
protected static DateOnly ResolveExpiryDate(DateOnly scheduledExpiry)
{
    var candidate = scheduledExpiry;
    while (IsNonTradingDay(candidate))
        candidate = candidate.AddDays(-1);
    return candidate;
}

    // ── Shared expiry calculators (called by subclass GetNearestExpiry) ───────

    /// <summary>
    /// Returns the nearest upcoming Tuesday weekly expiry (NIFTY).
    /// Never returns today — same-day expiry entry is always avoided.
    /// Rolls back to prior trading day if Tuesday is a holiday.
    /// </summary>
    protected static DateOnly NearestTuesdayExpiry(DateTime istNow)
    {
        var today = DateOnly.FromDateTime(istNow);
        var daysUntilTue = ((int)DayOfWeek.Tuesday - (int)today.DayOfWeek + 7) % 7;
        if (daysUntilTue == 0) daysUntilTue = 7;

        return ResolveExpiryDate(today.AddDays(daysUntilTue));
    }

    /// <summary>
    /// Returns the nearest upcoming Wednesday weekly expiry (FINNIFTY).
    /// Never returns today — same-day expiry entry is always avoided.
    /// Rolls back to prior trading day if Wednesday is a holiday.
    /// </summary>
    protected static DateOnly NearestWednesdayExpiry(DateTime istNow)
    {
        var today        = DateOnly.FromDateTime(istNow);
        var daysUntilWed = ((int)DayOfWeek.Wednesday - (int)today.DayOfWeek + 7) % 7;
        if (daysUntilWed == 0) daysUntilWed = 7;

        var scheduled = today.AddDays(daysUntilWed);
        return ResolveExpiryDate(scheduled);  // rolls back if holiday
    }
}
