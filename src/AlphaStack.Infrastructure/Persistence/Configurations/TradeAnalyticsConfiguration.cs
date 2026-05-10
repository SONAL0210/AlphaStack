using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using AlphaStack.Domain.Entities;

namespace AlphaStack.Infrastructure.Persistence.Configurations;

public class TradeAnalyticsConfiguration : IEntityTypeConfiguration<TradeAnalytics>
{
    public void Configure(EntityTypeBuilder<TradeAnalytics> builder)
    {
        builder.ToTable("trade_analytics");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");

        // ── Identity ──────────────────────────────────────────────────────────
        builder.Property(x => x.TradeId).HasColumnName("trade_id").IsRequired();
        builder.Property(x => x.StrategyName).HasColumnName("strategy_name").HasMaxLength(100).IsRequired();
        builder.Property(x => x.EntryVariation).HasColumnName("entry_variation").HasMaxLength(50).IsRequired();
        builder.Property(x => x.ExitVariation).HasColumnName("exit_variation").HasMaxLength(50);

        // ── Market context ────────────────────────────────────────────────────
        builder.Property(x => x.SpotAtEntry).HasColumnName("spot_at_entry").HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(x => x.SpotAtExit).HasColumnName("spot_at_exit").HasColumnType("numeric(18,2)");
        builder.Property(x => x.VixAtEntry).HasColumnName("vix_at_entry").HasColumnType("numeric(6,2)").IsRequired();
        builder.Property(x => x.VixRegime).HasColumnName("vix_regime").HasMaxLength(20).IsRequired();
        builder.Property(x => x.MarketRegime).HasColumnName("market_regime").HasMaxLength(20).IsRequired();
        builder.Property(x => x.Ema20AtEntry).HasColumnName("ema20_at_entry").HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(x => x.Ema50AtEntry).HasColumnName("ema50_at_entry").HasColumnType("numeric(18,2)");
        builder.Property(x => x.AdrAtEntry).HasColumnName("adr_at_entry").HasColumnType("numeric(10,2)").IsRequired();
        builder.Property(x => x.AtrAtEntry).HasColumnName("atr_at_entry").HasColumnType("numeric(10,2)").IsRequired();
        builder.Property(x => x.AtrAverageAtEntry).HasColumnName("atr_average_at_entry").HasColumnType("numeric(10,2)").IsRequired();
        builder.Property(x => x.GapPercent).HasColumnName("gap_percent").HasColumnType("numeric(6,3)").IsRequired();

        // ── Strike / spread ───────────────────────────────────────────────────
        builder.Property(x => x.ShortStrike).HasColumnName("short_strike").HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(x => x.LongStrike).HasColumnName("long_strike").HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(x => x.SpreadWidth).HasColumnName("spread_width").HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(x => x.StrikeDistanceInAdr).HasColumnName("strike_distance_in_adr").HasColumnType("numeric(6,2)").IsRequired();
        builder.Property(x => x.AdrMultiplierUsed).HasColumnName("adr_multiplier_used").HasColumnType("numeric(4,2)").IsRequired();
        builder.Property(x => x.ExpiryDate).HasColumnName("expiry_date").IsRequired();
        builder.Property(x => x.DaysToExpiryAtEntry).HasColumnName("days_to_expiry_at_entry").IsRequired();

        // ── Premium / P&L ─────────────────────────────────────────────────────
        builder.Property(x => x.PremiumCollected).HasColumnName("premium_collected").HasColumnType("numeric(10,2)").IsRequired();
        builder.Property(x => x.PremiumCaptured).HasColumnName("premium_captured").HasColumnType("numeric(10,2)");
        builder.Property(x => x.MaxPossibleLoss).HasColumnName("max_possible_loss").HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(x => x.ProfitTargetRs).HasColumnName("profit_target_rs").HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(x => x.StopLossThresholdRs).HasColumnName("stop_loss_threshold_rs").HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(x => x.CapitalAtRisk).HasColumnName("capital_at_risk").HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(x => x.CapitalAtRiskPercent).HasColumnName("capital_at_risk_percent").HasColumnType("numeric(6,2)").IsRequired();

        // ── MTM ───────────────────────────────────────────────────────────────
        builder.Property(x => x.MaxMtmProfit).HasColumnName("max_mtm_profit").HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(x => x.MaxMtmLoss).HasColumnName("max_mtm_loss").HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(x => x.SpotTouchedShortStrike).HasColumnName("spot_touched_short_strike").IsRequired().HasDefaultValue(false);

        // ── Exit ──────────────────────────────────────────────────────────────
        builder.Property(x => x.ExitReason).HasColumnName("exit_reason").HasMaxLength(100);
        builder.Property(x => x.GrossPnL).HasColumnName("gross_pnl").HasColumnType("numeric(18,2)");
        builder.Property(x => x.Brokerage).HasColumnName("brokerage").HasColumnType("numeric(10,2)");
        builder.Property(x => x.NetPnL).HasColumnName("net_pnl").HasColumnType("numeric(18,2)");
        builder.Property(x => x.HoldingMinutes).HasColumnName("holding_minutes");

        // ── Simulation ────────────────────────────────────────────────────────
        builder.Property(x => x.SlippageRs).HasColumnName("slippage_rs").HasColumnType("numeric(8,2)");
        builder.Property(x => x.ExecutionDelayMs).HasColumnName("execution_delay_ms");

        // ── Audit ─────────────────────────────────────────────────────────────
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        // ── Relationship ──────────────────────────────────────────────────────
        builder.HasOne(x => x.Trade)
            .WithOne()
            .HasForeignKey<TradeAnalytics>(x => x.TradeId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.TradeId).IsUnique();
        builder.HasIndex(x => x.StrategyName);
        builder.HasIndex(x => x.EntryVariation);
        builder.HasIndex(x => x.VixRegime);
        builder.HasIndex(x => x.MarketRegime);
    }
}
