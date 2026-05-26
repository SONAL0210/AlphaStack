using AlphaStack.Domain.Common;

namespace AlphaStack.Domain.Entities;

/// <summary>
/// Represents a synthetic trade variant logged at every signal evaluation.
/// For each real entry signal, N variants are created by permuting:
///   ADR multiplier × Spread width × Profit target % × Stop loss multiplier
/// This gives ~180 data points per real signal day, enabling rapid
/// parameter optimisation without additional capital risk.
///
/// Exit outcomes are filled in by ShadowExitSimulatorJob inside PnLTrackerService.
/// </summary>
public class ShadowTrade : BaseEntity
{
    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>FK to trade_analytics.trade_id of the real trade, if one was executed. Null if signal was rejected.</summary>
    public Guid? RealSignalGroupId { get; private set; }

    public string StrategyName    { get; private set; } = default!;
    public string EntryVariation  { get; private set; } = default!;

    /// <summary>True if this variant matches the parameters actually executed in the real trade.</summary>
    public bool WasRealTrade      { get; private set; }
    
    /// <summary>True if the real strategy was blocked by an open position — shadow data still collected.</summary>
    public bool WasPositionBlocked { get; private set; } 
    /// <summary>True if the regime gate passed (EMA directional check for spreads, neutral band for IronCondor).</summary>
    public bool MarketRegimeValid { get; private set; } 

    // ── Market context (identical across all variants for the same signal) ────

    public DateTime EvaluatedAt { get; private set; }
    public decimal SpotAtEntry    { get; private set; }
    public decimal VixAtEntry     { get; private set; }
    public string  VixRegime      { get; private set; } = default!;
    public decimal Ema20AtEntry   { get; private set; }
    public decimal AdrAtEntry     { get; private set; }
    public decimal AtrAtEntry     { get; private set; }
    public decimal AtrAverageAtEntry { get; private set; }
    public decimal GapPercent     { get; private set; }
    public int     DaysToExpiry   { get; private set; }
    public DateOnly ExpiryDate    { get; private set; }

    // ── Parameter variant (what makes each row unique) ────────────────────────

    public decimal AdrMultiplierUsed    { get; private set; }
    public int     SpreadWidth          { get; private set; }
    public decimal ProfitTargetPct      { get; private set; }
    public decimal StopLossMultiplier   { get; private set; }

    // ── Derived strikes & premium ─────────────────────────────────────────────

    public decimal ShortStrike          { get; private set; }
    public decimal LongStrike           { get; private set; }
    public decimal PremiumCollected     { get; private set; }
    public decimal ProfitTargetRs       { get; private set; }
    public decimal StopLossThresholdRs  { get; private set; }

    // ── Exit outcome (filled by ShadowExitSimulatorJob) ──────────────────────

    public string?  ExitReason          { get; private set; }
    public DateOnly? ExitDate           { get; private set; }
    public int?     HoldingMinutes      { get; private set; }
    public decimal? PremiumAtExit       { get; private set; }
    public decimal? GrossPnL            { get; private set; }

    /// <summary>Win | Loss | Open</summary>
    public string   Outcome             { get; private set; } = "Open";

    // ── Constructor ───────────────────────────────────────────────────────────

    private ShadowTrade() { }

    // ── Factory ───────────────────────────────────────────────────────────────

    public static ShadowTrade Create(
        Guid?   realSignalGroupId,
        string  strategyName,
        string  entryVariation,
        bool    wasRealTrade,
        bool    wasPositionBlocked, 
        bool    marketRegimeValid,
        // market context
        DateTime evaluatedAt,
        decimal spotAtEntry,
        decimal vixAtEntry,
        string  vixRegime,
        decimal ema20AtEntry,
        decimal adrAtEntry,
        decimal atrAtEntry,
        decimal atrAverageAtEntry,
        decimal gapPercent,
        int     daysToExpiry,
        DateOnly expiryDate,
        // variant params
        decimal adrMultiplierUsed,
        int     spreadWidth,
        decimal profitTargetPct,
        decimal stopLossMultiplier,
        // derived
        decimal shortStrike,
        decimal longStrike,
        decimal premiumCollected,
        int     quantity)
    {
        var profitTargetRs      = premiumCollected * quantity * profitTargetPct;
        var stopLossThresholdRs = premiumCollected * quantity * stopLossMultiplier;

        return new ShadowTrade
        {
            RealSignalGroupId    = realSignalGroupId,
            StrategyName         = strategyName,
            EntryVariation       = entryVariation,
            WasRealTrade         = wasRealTrade,
            WasPositionBlocked   = wasPositionBlocked,
            MarketRegimeValid    = marketRegimeValid,
            EvaluatedAt          = evaluatedAt,
            SpotAtEntry          = spotAtEntry,
            VixAtEntry           = vixAtEntry,
            VixRegime            = vixRegime,
            Ema20AtEntry         = ema20AtEntry,
            AdrAtEntry           = adrAtEntry,
            AtrAtEntry           = atrAtEntry,
            AtrAverageAtEntry    = atrAverageAtEntry,
            GapPercent           = gapPercent,
            DaysToExpiry         = daysToExpiry,
            ExpiryDate           = expiryDate,
            AdrMultiplierUsed    = adrMultiplierUsed,
            SpreadWidth          = spreadWidth,
            ProfitTargetPct      = profitTargetPct,
            StopLossMultiplier   = stopLossMultiplier,
            ShortStrike          = shortStrike,
            LongStrike           = longStrike,
            PremiumCollected     = premiumCollected,
            ProfitTargetRs       = profitTargetRs,
            StopLossThresholdRs  = stopLossThresholdRs,
            Outcome              = "Open",
        };
    }

    // ── Exit fill (called by ShadowExitSimulatorJob) ─────────────────────────

    public void CloseWithOutcome(
        string  exitReason,
        decimal premiumAtExit,
        int     quantity)
    {
        var spreadValueAtExit = premiumAtExit;
        GrossPnL        = (PremiumCollected - spreadValueAtExit) * quantity;
        ExitReason      = exitReason;
        ExitDate        = DateOnly.FromDateTime(DateTime.UtcNow);
        HoldingMinutes  = (int)(DateTime.UtcNow - EvaluatedAt).TotalMinutes;
        PremiumAtExit   = premiumAtExit;
        Outcome         = GrossPnL >= 0 ? "Win" : "Loss";
        MarkUpdated();
    }
}
