-- =============================================================================
-- AlphaStack — Comprehensive DB Fix Script
-- Run this ONCE to fix all schema mismatches found during testing
-- psql -U sonalsourav -d trading_platform -f fix_database.sql
-- =============================================================================

-- ── Fix 1: Run migration 002 (trades table was never created) ─────────────────
CREATE TABLE IF NOT EXISTS trades (
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

CREATE INDEX IF NOT EXISTS idx_trades_execution    ON trades(strategy_execution_id);
CREATE INDEX IF NOT EXISTS idx_trades_entry_signal ON trades(entry_signal_group_id);
CREATE INDEX IF NOT EXISTS idx_trades_status       ON trades(status);

-- ── Fix 2: Run migration 003 (client_order_id column missing from trade_orders) ─
ALTER TABLE trade_orders ADD COLUMN IF NOT EXISTS client_order_id VARCHAR(100);

CREATE UNIQUE INDEX IF NOT EXISTS idx_trade_orders_client_order_id
    ON trade_orders(client_order_id)
    WHERE client_order_id IS NOT NULL;

-- ── Fix 3: positions table missing unrealized_pnl column ─────────────────────
-- (Code references UnrealizedPnL on positions for PnL tracking)
ALTER TABLE positions ADD COLUMN IF NOT EXISTS unrealized_pnl NUMERIC(18,2) NOT NULL DEFAULT 0;

-- ── Fix 4: Verify enum values stored correctly ────────────────────────────────
-- The code uses HasConversion<string>() which stores enum NAME not custom string
-- option_type must be 'Put' or 'Call' (NOT 'PE'/'CE')
-- instrument_type must be 'FuturesAndOptions' (NOT 'OPT')
-- Correct the test instruments we inserted earlier
UPDATE instruments 
SET option_type = 'Put', instrument_type = 'FuturesAndOptions'
WHERE instrument_token IN (12345678, 12345679);

-- ── Verify everything looks correct ──────────────────────────────────────────
SELECT 'Tables:' as check;
SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename;

SELECT 'trade_orders columns:' as check;
SELECT column_name, data_type, is_nullable 
FROM information_schema.columns 
WHERE table_name = 'trade_orders' 
ORDER BY ordinal_position;

SELECT 'Instruments:' as check;
SELECT trading_symbol, option_type, instrument_type, strike_price, expiry_date 
FROM instruments;
