-- Migration 013_add_lot_size.sql
ALTER TABLE trade_analytics
    ADD COLUMN IF NOT EXISTS lot_size INTEGER NOT NULL DEFAULT 65;

ALTER TABLE shadow_trades  
    ADD COLUMN IF NOT EXISTS lot_size INTEGER NOT NULL DEFAULT 65;

-- Backfill existing rows
UPDATE trade_analytics SET lot_size = 65;
UPDATE shadow_trades SET lot_size = quantity;