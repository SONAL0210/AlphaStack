-- Migration: 007_add_banknifty_strategies.sql
-- Adds BankNifty Bull Put Spread and Bear Call Spread strategy definitions.
-- Uses ON CONFLICT (strategy_type) DO NOTHING so re-running is safe.

INSERT INTO strategy_definitions (
    id,
    name,
    description,
    strategy_type,
    market_regime,
    stage,
    version,
    parameters_json,
    is_active,
    created_at,
    updated_at
)
VALUES
(
    uuid_generate_v4(),
    'BankNifty Bull Put Spread',
    'Bull put spread on BANKNIFTY index. Wednesday weekly expiry. Bullish / low-vol regime. Spread width 400pts.',
    'BankNiftyBullPutSpread',
    'BullishLowVol',
    'PaperTrading',
    1,
    '{}',
    true,
    NOW(),
    NOW()
),
(
    uuid_generate_v4(),
    'BankNifty Bear Call Spread',
    'Bear call spread on BANKNIFTY index. Wednesday weekly expiry. Bearish / low-vol regime. Spread width 400pts.',
    'BankNiftyBearCallSpread',
    'BearishLowVol',
    'PaperTrading',
    1,
    '{}',
    true,
    NOW(),
    NOW()
)
ON CONFLICT (strategy_type) DO NOTHING;
