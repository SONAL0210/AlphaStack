ALTER TABLE shadow_trades
    ADD COLUMN IF NOT EXISTS shadow_group_id UUID;

CREATE INDEX IF NOT EXISTS idx_shadow_trades_shadow_group_id 
    ON shadow_trades (shadow_group_id)
    WHERE shadow_group_id IS NOT NULL;