-- Migration 011: add fees and net PnL to shadow_trades
ALTER TABLE shadow_trades
    ADD COLUMN IF NOT EXISTS fees_rs NUMERIC(12,2),
    ADD COLUMN IF NOT EXISTS net_pnl_rs NUMERIC(12,2);

-- Backfill existing closed rows with approximate fees (flat brokerage only)
UPDATE shadow_trades
SET fees_rs = CASE
        WHEN strategy_name ILIKE '%IronCondor%' THEN 160  -- 4 legs × ₹20 × 2
        ELSE 80                                            -- 2 legs × ₹20 × 2
    END,
    net_pnl_rs = gross_pnl - CASE
        WHEN strategy_name ILIKE '%IronCondor%' THEN 160
        ELSE 80
    END
WHERE exit_reason IS NOT NULL
  AND fees_rs IS NULL;