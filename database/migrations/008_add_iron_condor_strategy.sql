-- Migration: 008_add_iron_condor_strategy.sql
-- Adds the NIFTY Iron Condor strategy definition.
-- Idempotent: ON CONFLICT (strategy_type) DO NOTHING.

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
VALUES (
    uuid_generate_v4(),
    'NIFTY Iron Condor',
    'Iron Condor on NIFTY index. Tuesday weekly expiry, entered on Wednesday. '
    'Simultaneously sells an OTM put spread and an OTM call spread. '
    'Requires VIX 14–20 (range-bound) and spot within EMA20 ± 0.5×ADR (neutral). '
    'Spread width 200pts each wing, profit target 50%, stop loss 2× combined credit.',
    'NiftyIronCondor',
    'NeutralLowVol',
    'PaperTrading',
    1,
    '{}',
    true,
    NOW(),
    NOW()
)
ON CONFLICT (strategy_type) DO NOTHING;
