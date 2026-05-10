-- =============================================================================
-- Migration 002 — Add Trade aggregate table
-- Run against PostgreSQL:
--   psql -d trading_platform_dev -f database/migrations/002_add_trade_aggregate.sql
-- =============================================================================

CREATE TABLE trades (
    id                      UUID            PRIMARY KEY DEFAULT uuid_generate_v4(),
    strategy_execution_id   UUID            NOT NULL REFERENCES strategy_executions(id),
    symbol                  VARCHAR(50)     NOT NULL,
    direction               VARCHAR(10)     NOT NULL,
    status                  VARCHAR(20)     NOT NULL DEFAULT 'Created',
    entry_price             NUMERIC(18,4)   NOT NULL DEFAULT 0,
    exit_price              NUMERIC(18,4),
    quantity                NUMERIC(18,4)   NOT NULL,
    realized_pnl            NUMERIC(18,2),
    entry_time              TIMESTAMPTZ,
    exit_time               TIMESTAMPTZ,
    created_at              TIMESTAMPTZ     NOT NULL DEFAULT NOW(),
    updated_at              TIMESTAMPTZ,
    entry_signal_group_id   UUID            NOT NULL,
    exit_signal_group_id    UUID,
    entry_client_order_id   VARCHAR(100),
    exit_client_order_id    VARCHAR(100)
);

CREATE INDEX idx_trades_execution      ON trades(strategy_execution_id);
CREATE INDEX idx_trades_entry_signal   ON trades(entry_signal_group_id);
CREATE INDEX idx_trades_status         ON trades(status);
