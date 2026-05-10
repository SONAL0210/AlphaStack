using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using AlphaStack.Domain.Entities;
using AlphaStack.Domain.Enums;

namespace AlphaStack.Infrastructure.Persistence.Configurations;

public class UserProfileConfiguration : IEntityTypeConfiguration<UserProfile>
{
    public void Configure(EntityTypeBuilder<UserProfile> builder)
    {
        builder.ToTable("user_profiles");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.Username).HasColumnName("username").HasMaxLength(100).IsRequired();
        builder.Property(x => x.Email).HasColumnName("email").HasMaxLength(256).IsRequired();
        builder.Property(x => x.EncryptedKiteApiKey)
            .HasColumnName("encrypted_kite_api_key")
            .IsRequired(false);

        builder.Property(x => x.EncryptedKiteApiSecret)
            .HasColumnName("encrypted_kite_api_secret")
            .IsRequired(false);
        builder.Property(x => x.KiteAccessToken).HasColumnName("kite_access_token");
        builder.Property(x => x.KiteAccessTokenExpiry).HasColumnName("kite_access_token_expiry");
        builder.Property(x => x.EncryptedTelegramBotToken).HasColumnName("encrypted_telegram_bot_token").IsRequired();
        builder.Property(x => x.TelegramChatId).HasColumnName("telegram_chat_id");
        builder.Property(x => x.TotalCapitalAllocated).HasColumnName("total_capital_allocated").HasPrecision(18, 2);
        builder.Property(x => x.MaxDrawdownPercent).HasColumnName("max_drawdown_percent").HasPrecision(5, 2);
        builder.Property(x => x.MaxCapitalPerTradePercent).HasColumnName("max_capital_per_trade_percent").HasPrecision(5, 2);
        builder.Property(x => x.IsActive).HasColumnName("is_active");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        builder.Property(x => x.FyersClientId)
            .HasColumnName("fyers_client_id")
            .HasMaxLength(50)
            .IsRequired(false);

        builder.Property(x => x.EncryptedFyersSecret)
            .HasColumnName("encrypted_fyers_secret")
            .HasMaxLength(500)
            .IsRequired(false);

        builder.Property(x => x.FyersAccessToken)
            .HasColumnName("fyers_access_token")
            .IsRequired(false);

        builder.Property(x => x.FyersTokenSetAt)
            .HasColumnName("fyers_token_set_at")
            .IsRequired(false);

        builder.HasIndex(x => x.Username).IsUnique();
        builder.HasIndex(x => x.Email).IsUnique();
    }
}

public class StrategyDefinitionConfiguration : IEntityTypeConfiguration<StrategyDefinition>
{
    public void Configure(EntityTypeBuilder<StrategyDefinition> builder)
    {
        builder.ToTable("strategy_definitions");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(x => x.Description).HasColumnName("description").HasMaxLength(2000);
        builder.Property(x => x.StrategyType).HasColumnName("strategy_type").HasMaxLength(100).IsRequired();
        builder.Property(x => x.MarketRegime).HasColumnName("market_regime").HasMaxLength(100).IsRequired();
        builder.Property(x => x.Stage).HasColumnName("stage").HasConversion<string>();
        builder.Property(x => x.Version).HasColumnName("version");
        builder.Property(x => x.ParametersJson).HasColumnName("parameters_json").HasColumnType("jsonb");
        builder.Property(x => x.IsActive).HasColumnName("is_active");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        builder.HasIndex(x => x.StrategyType).IsUnique();
    }
}

public class StrategyExecutionConfiguration : IEntityTypeConfiguration<StrategyExecution>
{
    public void Configure(EntityTypeBuilder<StrategyExecution> builder)
    {
        builder.ToTable("strategy_executions");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.UserProfileId).HasColumnName("user_profile_id");
        builder.Property(x => x.StrategyDefinitionId).HasColumnName("strategy_definition_id");
        builder.Property(x => x.Mode).HasColumnName("mode").HasConversion<string>();
        builder.Property(x => x.IsRunning).HasColumnName("is_running");
        builder.Property(x => x.StartedAt).HasColumnName("started_at");
        builder.Property(x => x.StoppedAt).HasColumnName("stopped_at");
        builder.Property(x => x.AllocatedCapital).HasColumnName("allocated_capital").HasPrecision(18, 2);
        builder.Property(x => x.RealizedPnL).HasColumnName("realized_pnl").HasPrecision(18, 2);
        builder.Property(x => x.UnrealizedPnL).HasColumnName("unrealized_pnl").HasPrecision(18, 2);
        builder.Property(x => x.TotalTradesCount).HasColumnName("total_trades_count");
        builder.Property(x => x.WinningTradesCount).HasColumnName("winning_trades_count");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        builder.HasOne(x => x.UserProfile)
            .WithMany(x => x.StrategyExecutions)
            .HasForeignKey(x => x.UserProfileId);

        builder.HasOne(x => x.StrategyDefinition)
            .WithMany(x => x.Executions)
            .HasForeignKey(x => x.StrategyDefinitionId);

        builder.HasIndex(x => new { x.UserProfileId, x.StrategyDefinitionId, x.Mode }).IsUnique();
    }
}

public class TradeOrderConfiguration : IEntityTypeConfiguration<TradeOrder>
{
    public void Configure(EntityTypeBuilder<TradeOrder> builder)
    {
        builder.ToTable("trade_orders");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.StrategyExecutionId).HasColumnName("strategy_execution_id");
        builder.Property(x => x.Mode).HasColumnName("mode").HasConversion<string>();
        builder.Property(x => x.SignalGroupId).HasColumnName("signal_group_id");
        builder.Property(x => x.TradingSymbol).HasColumnName("trading_symbol").HasMaxLength(50).IsRequired();
        builder.Property(x => x.Exchange).HasColumnName("exchange").HasMaxLength(10).IsRequired();
        builder.Property(x => x.InstrumentToken).HasColumnName("instrument_token");
        builder.Property(x => x.InstrumentType).HasColumnName("instrument_type").HasConversion<string>();
        builder.Property(x => x.OptionType).HasColumnName("option_type").HasConversion<string>();
        builder.Property(x => x.StrikePrice).HasColumnName("strike_price").HasPrecision(18, 2);
        builder.Property(x => x.ExpiryDate).HasColumnName("expiry_date");
        builder.Property(x => x.Side).HasColumnName("side").HasConversion<string>();
        builder.Property(x => x.OrderType).HasColumnName("order_type").HasConversion<string>();
        builder.Property(x => x.Quantity).HasColumnName("quantity");
        builder.Property(x => x.LimitPrice).HasColumnName("limit_price").HasPrecision(18, 2);
        builder.Property(x => x.TriggerPrice).HasColumnName("trigger_price").HasPrecision(18, 2);
        builder.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
        builder.Property(x => x.FilledPrice).HasColumnName("filled_price").HasPrecision(18, 2);
        builder.Property(x => x.FilledQuantity).HasColumnName("filled_quantity");
        builder.Property(x => x.FilledAt).HasColumnName("filled_at");
        builder.Property(x => x.BrokerOrderId).HasColumnName("broker_order_id").HasMaxLength(100);
        builder.Property(x => x.TelegramMessageId).HasColumnName("telegram_message_id").HasMaxLength(100);
        builder.Property(x => x.ApprovalRequestedAt).HasColumnName("approval_requested_at");
        builder.Property(x => x.ApprovedAt).HasColumnName("approved_at");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.Property(x => x.ClientOrderId).HasColumnName("client_order_id");
        
        builder.HasOne(x => x.StrategyExecution)
            .WithMany(x => x.Orders)
            .HasForeignKey(x => x.StrategyExecutionId);

        builder.HasIndex(x => x.SignalGroupId);
        builder.HasIndex(x => x.Status);
        builder.HasIndex(x => x.BrokerOrderId);
    }
}

public class TradeConfiguration : IEntityTypeConfiguration<Trade>
{
    public void Configure(EntityTypeBuilder<Trade> builder)
    {
        builder.ToTable("trades");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.StrategyExecutionId).HasColumnName("strategy_execution_id");
        builder.Property(x => x.Symbol).HasColumnName("symbol").HasMaxLength(50).IsRequired();
        builder.Property(x => x.Direction).HasColumnName("direction").HasConversion<string>();
        builder.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
        builder.Property(x => x.EntryPrice).HasColumnName("entry_price").HasPrecision(18, 4);
        builder.Property(x => x.ExitPrice).HasColumnName("exit_price").HasPrecision(18, 4);
        builder.Property(x => x.Quantity).HasColumnName("quantity").HasPrecision(18, 4);
        builder.Property(x => x.RealizedPnL).HasColumnName("realized_pnl").HasPrecision(18, 2);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.EntryTime).HasColumnName("entry_time");
        builder.Property(x => x.ExitTime).HasColumnName("exit_time");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.Property(x => x.EntrySignalGroupId).HasColumnName("entry_signal_group_id");
        builder.Property(x => x.ExitSignalGroupId).HasColumnName("exit_signal_group_id");
        builder.Property(x => x.EntryClientOrderId).HasColumnName("entry_client_order_id").HasMaxLength(100);
        builder.Property(x => x.ExitClientOrderId).HasColumnName("exit_client_order_id").HasMaxLength(100);

        builder.HasOne(x => x.StrategyExecution)
            .WithMany()
            .HasForeignKey(x => x.StrategyExecutionId);

        builder.HasIndex(x => x.StrategyExecutionId).HasDatabaseName("idx_trades_execution");
        builder.HasIndex(x => x.EntrySignalGroupId).HasDatabaseName("idx_trades_entry_signal");
        builder.HasIndex(x => x.Status).HasDatabaseName("idx_trades_status");
    }
}

public class PositionConfiguration : IEntityTypeConfiguration<Position>
{
    public void Configure(EntityTypeBuilder<Position> builder)
    {
        builder.ToTable("positions");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.StrategyExecutionId).HasColumnName("strategy_execution_id");
        builder.Property(x => x.Mode).HasColumnName("mode").HasConversion<string>();
        builder.Property(x => x.SignalGroupId).HasColumnName("signal_group_id");
        builder.Property(x => x.TradingSymbol).HasColumnName("trading_symbol").HasMaxLength(50).IsRequired();
        builder.Property(x => x.Exchange).HasColumnName("exchange").HasMaxLength(10).IsRequired();
        builder.Property(x => x.InstrumentToken).HasColumnName("instrument_token");
        builder.Property(x => x.OptionType).HasColumnName("option_type").HasConversion<string>();
        builder.Property(x => x.StrikePrice).HasColumnName("strike_price").HasPrecision(18, 2);
        builder.Property(x => x.ExpiryDate).HasColumnName("expiry_date");
        builder.Property(x => x.Side).HasColumnName("side").HasConversion<string>();
        builder.Property(x => x.Quantity).HasColumnName("quantity");
        builder.Property(x => x.EntryPrice).HasColumnName("entry_price").HasPrecision(18, 2);
        builder.Property(x => x.ExitPrice).HasColumnName("exit_price").HasPrecision(18, 2);
        builder.Property(x => x.CurrentPrice).HasColumnName("current_price").HasPrecision(18, 2);
        builder.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
        builder.Property(x => x.OpenedAt).HasColumnName("opened_at");
        builder.Property(x => x.ClosedAt).HasColumnName("closed_at");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        builder.HasOne(x => x.StrategyExecution)
            .WithMany(x => x.Positions)
            .HasForeignKey(x => x.StrategyExecutionId);

        builder.HasIndex(x => x.SignalGroupId);
        builder.HasIndex(x => new { x.StrategyExecutionId, x.Status });
    }
}

public class InstrumentConfiguration : IEntityTypeConfiguration<Instrument>
{
    public void Configure(EntityTypeBuilder<Instrument> builder)
    {
        builder.ToTable("instruments");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.InstrumentToken).HasColumnName("instrument_token");
        builder.Property(x => x.TradingSymbol).HasColumnName("trading_symbol").HasMaxLength(50).IsRequired();
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(x => x.Exchange).HasColumnName("exchange").HasMaxLength(10).IsRequired();
        builder.Property(x => x.InstrumentType).HasColumnName("instrument_type").HasConversion<string>();
        builder.Property(x => x.OptionType).HasColumnName("option_type").HasConversion<string>();
        builder.Property(x => x.StrikePrice).HasColumnName("strike_price").HasPrecision(18, 2);
        builder.Property(x => x.ExpiryDate).HasColumnName("expiry_date");
        builder.Property(x => x.LotSize).HasColumnName("lot_size").HasPrecision(18, 2);
        builder.Property(x => x.TickSize).HasColumnName("tick_size").HasPrecision(18, 4);
        builder.Property(x => x.LastSyncedAt).HasColumnName("last_synced_at");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        builder.HasIndex(x => x.InstrumentToken).IsUnique();
        builder.HasIndex(x => new { x.TradingSymbol, x.Exchange });
        builder.HasIndex(x => new { x.Name, x.Exchange, x.ExpiryDate, x.OptionType, x.StrikePrice });
    }
}

public class ShadowTradeConfiguration : IEntityTypeConfiguration<ShadowTrade>
{
    public void Configure(EntityTypeBuilder<ShadowTrade> builder)
    {
        builder.ToTable("shadow_trades");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");

        builder.Property(x => x.RealSignalGroupId).HasColumnName("real_signal_group_id");
        builder.Property(x => x.StrategyName).HasColumnName("strategy_name").HasMaxLength(100).IsRequired();
        builder.Property(x => x.EntryVariation).HasColumnName("entry_variation").HasMaxLength(100).IsRequired();
        builder.Property(x => x.WasRealTrade).HasColumnName("was_real_trade");

        // Market context
        builder.Property(x => x.EvaluatedAt).HasColumnName("evaluated_at");
        builder.Property(x => x.SpotAtEntry).HasColumnName("spot_at_entry").HasPrecision(18, 2);
        builder.Property(x => x.VixAtEntry).HasColumnName("vix_at_entry").HasPrecision(8, 2);
        builder.Property(x => x.VixRegime).HasColumnName("vix_regime").HasMaxLength(20);
        builder.Property(x => x.Ema20AtEntry).HasColumnName("ema20_at_entry").HasPrecision(18, 2);
        builder.Property(x => x.AdrAtEntry).HasColumnName("adr_at_entry").HasPrecision(10, 2);
        builder.Property(x => x.AtrAtEntry).HasColumnName("atr_at_entry").HasPrecision(10, 2);
        builder.Property(x => x.AtrAverageAtEntry).HasColumnName("atr_average_at_entry").HasPrecision(10, 2);
        builder.Property(x => x.GapPercent).HasColumnName("gap_percent").HasPrecision(8, 4);
        builder.Property(x => x.DaysToExpiry).HasColumnName("days_to_expiry");
        builder.Property(x => x.ExpiryDate).HasColumnName("expiry_date");

        // Variant parameters
        builder.Property(x => x.AdrMultiplierUsed).HasColumnName("adr_multiplier_used").HasPrecision(5, 2);
        builder.Property(x => x.SpreadWidth).HasColumnName("spread_width");
        builder.Property(x => x.ProfitTargetPct).HasColumnName("profit_target_pct").HasPrecision(5, 2);
        builder.Property(x => x.StopLossMultiplier).HasColumnName("stop_loss_multiplier").HasPrecision(5, 2);

        // Derived strikes
        builder.Property(x => x.ShortStrike).HasColumnName("short_strike").HasPrecision(18, 2);
        builder.Property(x => x.LongStrike).HasColumnName("long_strike").HasPrecision(18, 2);
        builder.Property(x => x.PremiumCollected).HasColumnName("premium_collected").HasPrecision(10, 2);
        builder.Property(x => x.ProfitTargetRs).HasColumnName("profit_target_rs").HasPrecision(12, 2);
        builder.Property(x => x.StopLossThresholdRs).HasColumnName("stop_loss_threshold_rs").HasPrecision(12, 2);

        // Exit outcome
        builder.Property(x => x.ExitReason).HasColumnName("exit_reason").HasMaxLength(100);
        builder.Property(x => x.ExitDate).HasColumnName("exit_date");
        builder.Property(x => x.HoldingMinutes).HasColumnName("holding_minutes");
        builder.Property(x => x.PremiumAtExit).HasColumnName("premium_at_exit").HasPrecision(10, 2);
        builder.Property(x => x.GrossPnL).HasColumnName("gross_pnl").HasPrecision(12, 2);
        builder.Property(x => x.Outcome).HasColumnName("outcome").HasMaxLength(10);

        // Base entity
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        // Indexes — query patterns are: open trades, by signal group, by strategy+date
        builder.HasIndex(x => x.Outcome).HasDatabaseName("idx_shadow_trades_outcome");
        builder.HasIndex(x => x.RealSignalGroupId).HasDatabaseName("idx_shadow_trades_signal_group");
        builder.HasIndex(x => new { x.StrategyName, x.EvaluatedAt })
            .HasDatabaseName("idx_shadow_trades_strategy_date");
    }
}
