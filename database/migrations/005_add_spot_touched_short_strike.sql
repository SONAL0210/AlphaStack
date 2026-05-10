-- =============================================================================
-- Migration 005 — Add spot_touched_short_strike to trade_analytics
-- Tracks whether Nifty spot ever touched or crossed the short strike
-- during the life of the trade. Set by PnLTrackerService MTM cycle.
-- =============================================================================

ALTER TABLE trade_analytics
    ADD COLUMN IF NOT EXISTS spot_touched_short_strike BOOLEAN NOT NULL DEFAULT FALSE;

COMMENT ON COLUMN trade_analytics.spot_touched_short_strike IS
    'True if Nifty spot touched or crossed the short strike at any point. '
    'Set by PnLTrackerService. Idempotent — once true stays true.';
