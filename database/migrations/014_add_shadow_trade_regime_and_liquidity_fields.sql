-- Migration: add market_regime, short_leg_spread_pct, vix_rate_of_change to shadow_trades
-- Adds the three new data points agreed on (market regime label, bid-ask liquidity,
-- VIX rate-of-change). All nullable — existing rows are unaffected, new fields only
-- populate going forward once ShadowTradeLoggerService.cs is deployed with this change.

ALTER TABLE shadow_trades
    ADD COLUMN market_regime         varchar(20),
    ADD COLUMN short_leg_spread_pct  numeric(8,4),
    ADD COLUMN vix_rate_of_change    numeric(8,4);
