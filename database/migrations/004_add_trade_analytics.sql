-- =============================================================================
-- Migration 004 — Trade Analytics
-- Research data table for post-trade analysis and CSV export.
-- 1:1 with trades table. Populated at entry, updated at exit.
-- =============================================================================

CREATE TABLE trade_analytics (
    id                          UUID            PRIMARY KEY DEFAULT uuid_generate_v4(),

    -- ── Identity ──────────────────────────────────────────────────────────────
    trade_id                    UUID            NOT NULL REFERENCES trades(id),
    strategy_name               VARCHAR(100)    NOT NULL,
    entry_variation             VARCHAR(50)     NOT NULL,   -- MondayEntry | FridayEntry | IntradayAfter30Min ...
    exit_variation              VARCHAR(50),                -- ProfitTarget50 | StopLoss2x | ExpiryClose ...

    -- ── Market context at entry ───────────────────────────────────────────────
    spot_at_entry               NUMERIC(18,2)   NOT NULL,
    spot_at_exit                NUMERIC(18,2),
    vix_at_entry                NUMERIC(6,2)    NOT NULL,
    vix_regime                  VARCHAR(20)     NOT NULL,   -- VIX_LOW | VIX_MID | VIX_HIGH
    market_regime               VARCHAR(20)     NOT NULL,   -- TrendUp | TrendDown | Range
    ema20_at_entry              NUMERIC(18,2)   NOT NULL,
    ema50_at_entry              NUMERIC(18,2),
    adr_at_entry                NUMERIC(10,2)   NOT NULL,   -- avg daily range in points
    atr_at_entry                NUMERIC(10,2)   NOT NULL,   -- ATR(14) in points
    atr_average_at_entry        NUMERIC(10,2)   NOT NULL,   -- 20-day ATR avg
    gap_percent                 NUMERIC(6,3)    NOT NULL,   -- today's gap from prev close %

    -- ── Strike / spread details ───────────────────────────────────────────────
    short_strike                NUMERIC(18,2)   NOT NULL,
    long_strike                 NUMERIC(18,2)   NOT NULL,
    spread_width                NUMERIC(18,2)   NOT NULL,
    strike_distance_in_adr      NUMERIC(6,2)    NOT NULL,   -- (spot - short_strike) / ADR
    adr_multiplier_used         NUMERIC(4,2)    NOT NULL,   -- 1.2 | 1.5 | 2.0
    expiry_date                 DATE            NOT NULL,
    days_to_expiry_at_entry     INTEGER         NOT NULL,

    -- ── Premium / P&L ─────────────────────────────────────────────────────────
    premium_collected           NUMERIC(10,2)   NOT NULL,   -- net credit per unit
    premium_captured            NUMERIC(10,2),              -- filled at exit (null until closed)
    max_possible_loss           NUMERIC(18,2)   NOT NULL,   -- (spread_width - premium) × qty
    profit_target_rs            NUMERIC(18,2)   NOT NULL,   -- 50% of total credit
    stop_loss_threshold_rs      NUMERIC(18,2)   NOT NULL,   -- 2× total credit
    capital_at_risk             NUMERIC(18,2)   NOT NULL,   -- same as max_possible_loss
    capital_at_risk_percent     NUMERIC(6,2)    NOT NULL,   -- % of allocated capital

    -- ── MTM tracking ──────────────────────────────────────────────────────────
    max_mtm_profit              NUMERIC(18,2)   NOT NULL DEFAULT 0,
    max_mtm_loss                NUMERIC(18,2)   NOT NULL DEFAULT 0,

    -- ── Exit details ──────────────────────────────────────────────────────────
    exit_reason                 VARCHAR(100),
    gross_pnl                   NUMERIC(18,2),
    brokerage                   NUMERIC(10,2),
    net_pnl                     NUMERIC(18,2),
    holding_minutes             INTEGER,

    -- ── Broker simulation ─────────────────────────────────────────────────────
    slippage_rs                 NUMERIC(8,2),
    execution_delay_ms          INTEGER,

    -- ── Audit ─────────────────────────────────────────────────────────────────
    created_at                  TIMESTAMPTZ     NOT NULL DEFAULT NOW(),
    updated_at                  TIMESTAMPTZ,

    CONSTRAINT uq_trade_analytics_trade_id UNIQUE (trade_id)
);

CREATE INDEX idx_trade_analytics_strategy     ON trade_analytics(strategy_name);
CREATE INDEX idx_trade_analytics_entry_var    ON trade_analytics(entry_variation);
CREATE INDEX idx_trade_analytics_vix_regime   ON trade_analytics(vix_regime);
CREATE INDEX idx_trade_analytics_market_reg   ON trade_analytics(market_regime);
CREATE INDEX idx_trade_analytics_expiry       ON trade_analytics(expiry_date);
CREATE INDEX idx_trade_analytics_created      ON trade_analytics(created_at);

-- ── View: ready for CSV export ────────────────────────────────────────────────
-- Use this query to export to CSV for analysis:
--
-- \COPY (SELECT * FROM v_trade_analytics_export ORDER BY created_at DESC) TO 'trades.csv' CSV HEADER;

CREATE OR REPLACE VIEW v_trade_analytics_export AS
SELECT
    ta.id,
    ta.strategy_name,
    ta.entry_variation,
    ta.exit_variation,
    ta.created_at::DATE                         AS entry_date,
    to_char(ta.created_at, 'HH24:MI')          AS entry_time,
    ta.holding_minutes,
    ta.market_regime,
    ta.vix_regime,
    ta.vix_at_entry,
    ta.spot_at_entry,
    ta.spot_at_exit,
    ta.ema20_at_entry,
    ta.adr_at_entry,
    ta.atr_at_entry,
    ta.gap_percent,
    ta.short_strike,
    ta.long_strike,
    ta.spread_width,
    ta.strike_distance_in_adr,
    ta.adr_multiplier_used,
    ta.days_to_expiry_at_entry,
    ta.premium_collected,
    ta.premium_captured,
    ta.profit_target_rs,
    ta.stop_loss_threshold_rs,
    ta.capital_at_risk,
    ta.capital_at_risk_percent,
    ta.max_mtm_profit,
    ta.max_mtm_loss,
    ta.exit_reason,
    ta.gross_pnl,
    ta.brokerage,
    ta.net_pnl,
    ta.slippage_rs,
    ta.execution_delay_ms,
    CASE WHEN ta.net_pnl > 0 THEN 'Win' ELSE 'Loss' END AS outcome
FROM trade_analytics ta
ORDER BY ta.created_at DESC;
