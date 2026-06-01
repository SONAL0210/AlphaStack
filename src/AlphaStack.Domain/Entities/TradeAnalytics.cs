using AlphaStack.Domain.Common;

namespace AlphaStack.Domain.Entities;

/// <summary>
/// Research data attached 1:1 to a Trade.
/// Populated at entry, updated at exit.
/// Used for post-trade analysis and CSV export.
///
/// Naming follows the OPTIONS RESEARCH FRAMEWORK spec:
///   Strategy variations : BullPutBaseline | BullPutVixAdaptive | BearCall | IronCondor
///   Entry variations    : MondayEntry | FridayEntry | MondaySameDayExit |
///                         FridayMondayExit | MondayExpiryHold | IntradayAfter30Min
///   Exit variations     : ProfitTarget50 | StopLoss2x | EndOfDay |
///                         MondayClose | ExpiryClose | StrikeBreach
///   Market regime       : TrendUp | TrendDown | Range
///   VIX regime          : Low (VIX<14) | Mid (14-18) | High (>18)
/// </summary>
public class TradeAnalytics : BaseEntity
{
    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>FK to trades.id — 1:1 relationship.</summary>
    public Guid TradeId { get; private set; }

    public string StrategyName { get; private set; } = default!;

    /// <summary>e.g. MondayEntry, FridayEntry, IntradayAfter30Min</summary>
    public string EntryVariation { get; private set; } = default!;

    /// <summary>e.g. ProfitTarget50, StopLoss2x, ExpiryClose</summary>
    public string? ExitVariation { get; private set; }

    // ── Market context at entry ───────────────────────────────────────────────

    public decimal SpotAtEntry { get; private set; }
    public decimal? SpotAtExit { get; private set; }
    public int LotSize { get; private set; }

    /// <summary>India VIX value at entry.</summary>
    public decimal VixAtEntry { get; private set; }

    /// <summary>VIX_LOW | VIX_MID | VIX_HIGH</summary>
    public string VixRegime { get; private set; } = default!;

    /// <summary>TrendUp | TrendDown | Range</summary>
    public string MarketRegime { get; private set; } = default!;

    /// <summary>EMA20 value at entry.</summary>
    public decimal Ema20AtEntry { get; private set; }

    /// <summary>EMA50 value at entry (populated when available).</summary>
    public decimal? Ema50AtEntry { get; private set; }

    /// <summary>Average Daily Range (pts) at entry — 20-day avg of (High - Low).</summary>
    public decimal AdrAtEntry { get; private set; }

    /// <summary>ATR (14-day) at entry.</summary>
    public decimal AtrAtEntry { get; private set; }

    /// <summary>20-day ATR average at entry — used for spike filter.</summary>
    public decimal AtrAverageAtEntry { get; private set; }

    /// <summary>Gap % from previous close to today's open. Positive = gap up.</summary>
    public decimal GapPercent { get; private set; }

    // ── Strike / spread details ───────────────────────────────────────────────

    public decimal ShortStrike { get; private set; }
    public decimal LongStrike { get; private set; }
    public decimal SpreadWidth { get; private set; }

    /// <summary>How far short strike is from spot, in ADR units. (spot - shortStrike) / ADR</summary>
    public decimal StrikeDistanceInAdr { get; private set; }

    /// <summary>ADR multiplier actually used for strike selection.</summary>
    public decimal AdrMultiplierUsed { get; private set; }

    public DateOnly ExpiryDate { get; private set; }

    /// <summary>Calendar days from entry to expiry.</summary>
    public int DaysToExpiryAtEntry { get; private set; }

    // ── Premium / P&L ─────────────────────────────────────────────────────────

    /// <summary>Net credit per unit at entry (short premium - long premium).</summary>
    public decimal PremiumCollected { get; private set; }

    /// <summary>Premium captured at exit. Null until closed.</summary>
    public decimal? PremiumCaptured { get; private set; }

    /// <summary>Theoretical max loss = (SpreadWidth - PremiumCollected) × Qty.</summary>
    public decimal MaxPossibleLoss { get; private set; }

    /// <summary>Profit target in ₹ = PremiumCollected × Qty × 0.5</summary>
    public decimal ProfitTargetRs { get; private set; }

    /// <summary>Stop loss threshold in ₹ = PremiumCollected × Qty × 2</summary>
    public decimal StopLossThresholdRs { get; private set; }

    /// <summary>Capital at risk = MaxPossibleLoss (margin blocked).</summary>
    public decimal CapitalAtRisk { get; private set; }

    /// <summary>Capital at risk as % of execution's allocated capital.</summary>
    public decimal CapitalAtRiskPercent { get; private set; }

    // ── MTM tracking (updated by PnLTracker) ─────────────────────────────────

    /// <summary>Peak unrealized profit during the trade lifetime.</summary>
    public decimal MaxMtmProfit { get; private set; }

    /// <summary>Worst unrealized loss during the trade lifetime.</summary>
    public decimal MaxMtmLoss { get; private set; }

    /// <summary>
    /// True if Nifty spot touched or crossed the short strike at any point during the trade.
    /// Set by PnLTrackerService during MTM updates.
    /// A breach does not guarantee a loss — it means the position was under pressure.
    /// </summary>
    public bool SpotTouchedShortStrike { get; private set; }

    // ── Exit details ──────────────────────────────────────────────────────────

    public string? ExitReason { get; private set; }
    public decimal? GrossPnL { get; private set; }

    /// <summary>Estimated brokerage (paper: simulated at ₹20/order flat).</summary>
    public decimal? Brokerage { get; private set; }
    public decimal? NetPnL { get; private set; }

    /// <summary>Minutes from entry fill to exit fill.</summary>
    public int? HoldingMinutes { get; private set; }

    // ── Broker simulation ─────────────────────────────────────────────────────

    /// <summary>Simulated slippage in ₹ per unit (paper mode).</summary>
    public decimal? SlippageRs { get; private set; }

    /// <summary>Simulated execution delay in ms (paper mode).</summary>
    public int? ExecutionDelayMs { get; private set; }

    // ── Navigation ────────────────────────────────────────────────────────────

    public Trade Trade { get; private set; } = default!;

    // ── Constructor ───────────────────────────────────────────────────────────

    private TradeAnalytics() { }

    // ── Factory — call at entry fill ─────────────────────────────────────────

    public static TradeAnalytics CreateAtEntry(
        Guid tradeId,
        string strategyName,
        string entryVariation,
        decimal spotAtEntry,
        decimal vixAtEntry,
        string vixRegime,
        string marketRegime,
        decimal ema20AtEntry,
        decimal adrAtEntry,
        decimal atrAtEntry,
        decimal atrAverageAtEntry,
        decimal gapPercent,
        decimal shortStrike,
        decimal longStrike,
        DateOnly expiryDate,
        decimal premiumCollected,
        decimal quantity,
        decimal allocatedCapital,
        decimal adrMultiplierUsed,
        int? executionDelayMs = null,
        decimal? slippageRs = null,
        decimal? ema50AtEntry = null)
    {
        var spreadWidth      = Math.Abs(shortStrike - longStrike);
        var maxPossibleLoss  = (spreadWidth - premiumCollected) * quantity;
        var profitTarget     = premiumCollected * quantity * 0.5m;
        var stopLossThresh   = premiumCollected * quantity * 2.0m;
        var capitalAtRisk    = maxPossibleLoss;
        var capitalPct       = allocatedCapital > 0
            ? Math.Round(capitalAtRisk / allocatedCapital * 100, 2)
            : 0m;
        var strikeDistInAdr  = adrAtEntry > 0
            ? Math.Round((spotAtEntry - shortStrike) / adrAtEntry, 2)
            : 0m;
        var daysToExpiry     = expiryDate.DayNumber - DateOnly.FromDateTime(DateTime.Now).DayNumber;

        return new TradeAnalytics
        {
            TradeId = tradeId,
            StrategyName = strategyName,
            EntryVariation = entryVariation,
            SpotAtEntry = spotAtEntry,
            VixAtEntry = vixAtEntry,
            VixRegime = vixRegime,
            MarketRegime = marketRegime,
            Ema20AtEntry = ema20AtEntry,
            Ema50AtEntry = ema50AtEntry,
            AdrAtEntry = adrAtEntry,
            AtrAtEntry = atrAtEntry,
            AtrAverageAtEntry = atrAverageAtEntry,
            GapPercent = gapPercent,
            ShortStrike = shortStrike,
            LongStrike = longStrike,
            SpreadWidth = spreadWidth,
            StrikeDistanceInAdr = strikeDistInAdr,
            AdrMultiplierUsed = adrMultiplierUsed,
            ExpiryDate = expiryDate,
            DaysToExpiryAtEntry = Math.Max(0, daysToExpiry),
            PremiumCollected = premiumCollected,
            MaxPossibleLoss = maxPossibleLoss,
            ProfitTargetRs = profitTarget,
            StopLossThresholdRs = stopLossThresh,
            CapitalAtRisk = capitalAtRisk,
            CapitalAtRiskPercent = capitalPct,
            MaxMtmProfit = 0m,
            MaxMtmLoss = 0m,
            ExecutionDelayMs = executionDelayMs,
            SlippageRs = slippageRs,
            LotSize = (int)quantity / (strategyName.StartsWith("FINNIFTY") ? 40 : 65),
        };
    }

    // ── Update MTM (called by PnLTrackerService every 5 min) ─────────────────

    public void UpdateMtm(decimal currentPnL)
    {
        if (currentPnL > MaxMtmProfit) MaxMtmProfit = currentPnL;
        if (currentPnL < MaxMtmLoss)   MaxMtmLoss   = currentPnL;
        MarkUpdated();
    }

    // ── Strike breach tracking ───────────────────────────────────────────────

    /// <summary>
    /// Called by PnLTrackerService when spot touches or crosses the short strike.
    /// Idempotent — once true, stays true.
    /// </summary>
    public void MarkShortStrikeTouched()
    {
        if (!SpotTouchedShortStrike)
        {
            SpotTouchedShortStrike = true;
            MarkUpdated();
        }
    }

    // ── Close (called at exit fill) ───────────────────────────────────────────

    public void CloseAnalytics(
        decimal spotAtExit,
        string exitVariation,
        string exitReason,
        decimal premiumCaptured,
        decimal grossPnL,
        decimal brokerage,
        DateTimeOffset entryTime,
        DateTimeOffset exitTime)
    {
        SpotAtExit       = spotAtExit;
        ExitVariation    = exitVariation;
        ExitReason       = exitReason;
        PremiumCaptured  = premiumCaptured;
        GrossPnL         = grossPnL;
        Brokerage        = brokerage;
        NetPnL           = grossPnL - brokerage;
        HoldingMinutes   = (int)(exitTime - entryTime).TotalMinutes;
        MarkUpdated();
    }
}
