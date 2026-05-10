-- =============================================================================
-- Migration 007 — Multi-user preparation
-- Adds Fyers credentials to user_profiles for live multi-user trading.
-- Paper mode: single shared token in config remains sufficient.
-- Live mode (Month 2): per-user token flow activated using these columns.
-- =============================================================================

-- Fyers credentials per user
ALTER TABLE user_profiles
    ADD COLUMN IF NOT EXISTS fyers_client_id          VARCHAR(50)     NULL,
    ADD COLUMN IF NOT EXISTS encrypted_fyers_secret   VARCHAR(500)    NULL,
    ADD COLUMN IF NOT EXISTS fyers_access_token       TEXT            NULL,
    ADD COLUMN IF NOT EXISTS fyers_token_set_at       TIMESTAMPTZ     NULL;

COMMENT ON COLUMN user_profiles.fyers_client_id IS
    'Fyers app client ID for this user e.g. ABC123-100. Null until live trading.';

COMMENT ON COLUMN user_profiles.encrypted_fyers_secret IS
    'Fyers secret key encrypted at rest via DataProtection. Null until live trading.';

COMMENT ON COLUMN user_profiles.fyers_access_token IS
    'Current Fyers JWT access token. Refreshed daily via OAuth callback. Null until live trading.';

COMMENT ON COLUMN user_profiles.fyers_token_set_at IS
    'UTC timestamp when fyers_access_token was last refreshed. Used to detect stale tokens.';

-- Seed your existing user with your current Fyers client ID
-- Replace the username with yours
UPDATE user_profiles
SET fyers_client_id = '9UR22VT666-100'
WHERE username = 'sonalsourav';

-- Verify
SELECT id, username, fyers_client_id, fyers_token_set_at
FROM user_profiles;
