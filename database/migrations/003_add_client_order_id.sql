-- =============================================================================
-- Migration 003 — Add client_order_id to trade_orders
-- Run: psql -d trading_platform_dev -f database/migrations/003_add_client_order_id.sql
-- =============================================================================

ALTER TABLE trade_orders ADD COLUMN IF NOT EXISTS client_order_id VARCHAR(100);

-- Unique partial index: only enforces uniqueness when value is present
CREATE UNIQUE INDEX IF NOT EXISTS idx_trade_orders_client_order_id
    ON trade_orders(client_order_id)
    WHERE client_order_id IS NOT NULL;
