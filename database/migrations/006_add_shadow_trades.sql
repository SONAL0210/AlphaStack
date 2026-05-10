-- =============================================================================
-- Migration 006 — Add shadow_trades table
-- Stores synthetic trade variants logged at every signal evaluation.
-- 180 variants per real signal (5 ADR × 4 widths × 3 targets × 3 SL)
-- Exit outcomes filled by ShadowExitSimulatorJob in PnLTrackerService.
-- =============================================================================

CREATE TABLE IF NOT EXISTS shadow_trades (
    id                      UUID            PRIMARY KEY DEFAULT uuid_generate_v4(),

    -- Identity
    real_signal_group_id    UUID            NULL,        -- FK to real trade (null if signal rejected)
    strategy_name           VARCHAR(100)    NOT NULL,
    entry_variation         VARCHAR(100)    NOT NULL,
    was_real_trade          BOOLEAN         NOT NULL DEFAULT FALSE,

    -- Market context (same across all variants for one signal)
    evaluated_at            TIMESTAMPTZ     NOT NULL,
    spot_at_entry           NUMERIC(18,2)   NOT NULL,
    vix_at_entry            NUMERIC(8,2)    NOT NULL,
    vix_regime              VARCHAR(20)     NOT NULL,
    ema20_at_entry          NUMERIC(18,2)   NOT NULL,
    adr_at_entry            NUMERIC(10,2)   NOT NULL,
    atr_at_entry            NUMERIC(10,2)   NOT NULL,
    atr_average_at_entry    NUMERIC(10,2)   NOT NULL,
    gap_percent             NUMERIC(8,4)    NOT NULL,
    days_to_expiry          INT             NOT NULL,
    expiry_date             DATE            NOT NULL,

    -- Parameter variant
    adr_multiplier_used     NUMERIC(5,2)    NOT NULL,
    spread_width            INT             NOT NULL,
    profit_target_pct       NUMERIC(5,2)    NOT NULL,
    stop_loss_multiplier    NUMERIC(5,2)    NOT NULL,

    -- Derived strikes & premium
    short_strike            NUMERIC(18,2)   NOT NULL,
    long_strike             NUMERIC(18,2)   NOT NULL,
    premium_collected       NUMERIC(10,2)   NOT NULL,
    profit_target_rs        NUMERIC(12,2)   NOT NULL,
    stop_loss_threshold_rs  NUMERIC(12,2)   NOT NULL,

    -- Exit outcome (filled by ShadowExitSimulatorJob)
    exit_reason             VARCHAR(100)    NULL,
    exit_date               DATE            NULL,
    holding_minutes         INT             NULL,
    premium_at_exit         NUMERIC(10,2)   NULL,
    gross_pnl               NUMERIC(12,2)   NULL,
    outcome                 VARCHAR(10)     NOT NULL DEFAULT 'Open',

    -- Base entity
    created_at              TIMESTAMPTZ     NOT NULL DEFAULT NOW(),
    updated_at              TIMESTAMPTZ     NULL
);

-- Indexes for query patterns used by ShadowExitSimulatorJob + export
CREATE INDEX IF NOT EXISTS idx_shadow_trades_outcome
    ON shadow_trades(outcome);

CREATE INDEX IF NOT EXISTS idx_shadow_trades_signal_group
    ON shadow_trades(real_signal_group_id);

CREATE INDEX IF NOT EXISTS idx_shadow_trades_strategy_date
    ON shadow_trades(strategy_name, evaluated_at);

COMMENT ON TABLE shadow_trades IS
    'Synthetic trade variants logged at every signal evaluation. '
    'Each real entry signal generates ~180 rows covering the full parameter matrix. '
    'Exit outcomes filled automatically by ShadowExitSimulatorJob in PnLTrackerService.';
