-- Migration 015: Add live trading kill switch to user_profiles

ALTER TABLE user_profiles
    ADD COLUMN is_live_trading_halted BOOLEAN NOT NULL DEFAULT false,
    ADD COLUMN live_trading_halted_reason VARCHAR(500) NULL,
    ADD COLUMN live_trading_halted_at TIMESTAMP NULL;