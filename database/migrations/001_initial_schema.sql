-- =============================================================================
-- AlphaStack — Initial Schema
-- Run against PostgreSQL after creating the database:
--   CREATE DATABASE trading_platform_dev;
--   CREATE USER trading_user WITH ENCRYPTED PASSWORD 'dev_password';
--   GRANT ALL PRIVILEGES ON DATABASE trading_platform_dev TO trading_user;
-- =============================================================================

-- ── Extensions ────────────────────────────────────────────────────────────────
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";

-- ── user_profiles ─────────────────────────────────────────────────────────────
CREATE TABLE user_profiles (
    id                          UUID            PRIMARY KEY DEFAULT uuid_generate_v4(),
    username                    VARCHAR(100)    NOT NULL UNIQUE,
    email                       VARCHAR(256)    NOT NULL UNIQUE,
    encrypted_kite_api_key      TEXT            NOT NULL,
    encrypted_kite_api_secret   TEXT            NOT NULL,
    kite_access_token           TEXT,
    kite_access_token_expiry    TIMESTAMPTZ,
    encrypted_telegram_bot_token TEXT           NOT NULL,
    telegram_chat_id            BIGINT          NOT NULL,
    total_capital_allocated     NUMERIC(18,2)   NOT NULL,
    max_drawdown_percent        NUMERIC(5,2)    NOT NULL,
    max_capital_per_trade_percent NUMERIC(5,2)  NOT NULL,
    is_active                   BOOLEAN         NOT NULL DEFAULT TRUE,
    created_at                  TIMESTAMPTZ     NOT NULL DEFAULT NOW(),
    updated_at                  TIMESTAMPTZ
);

-- ── strategy_definitions ──────────────────────────────────────────────────────
CREATE TABLE strategy_definitions (
    id              UUID            PRIMARY KEY DEFAULT uuid_generate_v4(),
    name            VARCHAR(200)    NOT NULL,
    description     VARCHAR(2000),
    strategy_type   VARCHAR(100)    NOT NULL UNIQUE,
    market_regime   VARCHAR(100)    NOT NULL,
    stage           VARCHAR(50)     NOT NULL DEFAULT 'PaperTrading',
    version         INTEGER         NOT NULL DEFAULT 1,
    parameters_json JSONB           NOT NULL DEFAULT '{}',
    is_active       BOOLEAN         NOT NULL DEFAULT TRUE,
    created_at      TIMESTAMPTZ     NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ
);

-- ── strategy_executions ───────────────────────────────────────────────────────
CREATE TABLE strategy_executions (
    id                      UUID            PRIMARY KEY DEFAULT uuid_generate_v4(),
    user_profile_id         UUID            NOT NULL REFERENCES user_profiles(id),
    strategy_definition_id  UUID            NOT NULL REFERENCES strategy_definitions(id),
    mode                    VARCHAR(10)     NOT NULL CHECK (mode IN ('Paper', 'Live')),
    is_running              BOOLEAN         NOT NULL DEFAULT FALSE,
    started_at              TIMESTAMPTZ,
    stopped_at              TIMESTAMPTZ,
    allocated_capital       NUMERIC(18,2)   NOT NULL,
    realized_pnl            NUMERIC(18,2)   NOT NULL DEFAULT 0,
    unrealized_pnl          NUMERIC(18,2)   NOT NULL DEFAULT 0,
    total_trades_count      INTEGER         NOT NULL DEFAULT 0,
    winning_trades_count    INTEGER         NOT NULL DEFAULT 0,
    created_at              TIMESTAMPTZ     NOT NULL DEFAULT NOW(),
    updated_at              TIMESTAMPTZ,
    UNIQUE (user_profile_id, strategy_definition_id, mode)
);

CREATE INDEX idx_strategy_executions_running
    ON strategy_executions(is_running) WHERE is_running = TRUE;

-- ── trade_orders ──────────────────────────────────────────────────────────────
CREATE TABLE trade_orders (
    id                      UUID            PRIMARY KEY DEFAULT uuid_generate_v4(),
    strategy_execution_id   UUID            NOT NULL REFERENCES strategy_executions(id),
    mode                    VARCHAR(10)     NOT NULL CHECK (mode IN ('Paper', 'Live')),
    signal_group_id         UUID            NOT NULL,
    trading_symbol          VARCHAR(50)     NOT NULL,
    exchange                VARCHAR(10)     NOT NULL,
    instrument_token        INTEGER         NOT NULL,
    instrument_type         VARCHAR(30)     NOT NULL,
    option_type             VARCHAR(5),     -- CE or PE
    strike_price            NUMERIC(18,2),
    expiry_date             DATE,
    side                    VARCHAR(5)      NOT NULL CHECK (side IN ('Buy', 'Sell')),
    order_type              VARCHAR(20)     NOT NULL,
    quantity                INTEGER         NOT NULL,
    limit_price             NUMERIC(18,2),
    trigger_price           NUMERIC(18,2),
    status                  VARCHAR(20)     NOT NULL DEFAULT 'Pending',
    filled_price            NUMERIC(18,2),
    filled_quantity         INTEGER         NOT NULL DEFAULT 0,
    filled_at               TIMESTAMPTZ,
    broker_order_id         VARCHAR(100),
    telegram_message_id     VARCHAR(100),
    approval_requested_at   TIMESTAMPTZ,
    approved_at             TIMESTAMPTZ,
    created_at              TIMESTAMPTZ     NOT NULL DEFAULT NOW(),
    updated_at              TIMESTAMPTZ
);

CREATE INDEX idx_trade_orders_signal_group  ON trade_orders(signal_group_id);
CREATE INDEX idx_trade_orders_status        ON trade_orders(status);
CREATE INDEX idx_trade_orders_broker_id     ON trade_orders(broker_order_id) WHERE broker_order_id IS NOT NULL;

-- ── positions ─────────────────────────────────────────────────────────────────
CREATE TABLE positions (
    id                      UUID            PRIMARY KEY DEFAULT uuid_generate_v4(),
    strategy_execution_id   UUID            NOT NULL REFERENCES strategy_executions(id),
    mode                    VARCHAR(10)     NOT NULL CHECK (mode IN ('Paper', 'Live')),
    signal_group_id         UUID            NOT NULL,
    trading_symbol          VARCHAR(50)     NOT NULL,
    exchange                VARCHAR(10)     NOT NULL,
    instrument_token        INTEGER         NOT NULL,
    option_type             VARCHAR(5),
    strike_price            NUMERIC(18,2),
    expiry_date             DATE,
    side                    VARCHAR(5)      NOT NULL CHECK (side IN ('Buy', 'Sell')),
    quantity                INTEGER         NOT NULL,
    entry_price             NUMERIC(18,2)   NOT NULL,
    exit_price              NUMERIC(18,2),
    current_price           NUMERIC(18,2)   NOT NULL,
    status                  VARCHAR(10)     NOT NULL DEFAULT 'Open',
    opened_at               TIMESTAMPTZ     NOT NULL DEFAULT NOW(),
    closed_at               TIMESTAMPTZ,
    created_at              TIMESTAMPTZ     NOT NULL DEFAULT NOW(),
    updated_at              TIMESTAMPTZ
);

CREATE INDEX idx_positions_signal_group         ON positions(signal_group_id);
CREATE INDEX idx_positions_execution_status     ON positions(strategy_execution_id, status);

-- ── instruments ───────────────────────────────────────────────────────────────
CREATE TABLE instruments (
    id                  UUID            PRIMARY KEY DEFAULT uuid_generate_v4(),
    instrument_token    INTEGER         NOT NULL UNIQUE,
    trading_symbol      VARCHAR(50)     NOT NULL,
    name                VARCHAR(200)    NOT NULL,
    exchange            VARCHAR(10)     NOT NULL,
    instrument_type     VARCHAR(30)     NOT NULL,
    option_type         VARCHAR(5),
    strike_price        NUMERIC(18,2),
    expiry_date         DATE,
    lot_size            NUMERIC(18,2)   NOT NULL DEFAULT 1,
    tick_size           NUMERIC(18,4)   NOT NULL DEFAULT 0.05,
    last_synced_at      TIMESTAMPTZ     NOT NULL DEFAULT NOW(),
    created_at          TIMESTAMPTZ     NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ
);

CREATE INDEX idx_instruments_symbol_exchange
    ON instruments(trading_symbol, exchange);

CREATE INDEX idx_instruments_options_lookup
    ON instruments(name, exchange, expiry_date, option_type, strike_price)
    WHERE option_type IS NOT NULL;

-- ── Seed: Known strategies (paper trade directly per context.md) ───────────────
INSERT INTO strategy_definitions (id, name, description, strategy_type, market_regime, stage)
VALUES
    (uuid_generate_v4(),
     'Bull Put Spread',
     'Sell an OTM put, buy a further OTM put on same expiry. Profits when market stays above short strike. Low vol, mildly bullish regime.',
     'BullPutSpread',
     'LowVolatility_Bullish',
     'PaperTrading'),

    (uuid_generate_v4(),
     'Iron Condor',
     'Sell OTM call + put, buy further OTM call + put. Profits in range-bound low-vol market.',
     'IronCondor',
     'LowVolatility_RangeBound',
     'PaperTrading');
