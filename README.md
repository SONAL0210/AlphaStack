# AlphaStack v4

Semi-automated options trading platform for Indian index options.

---

## Overview

AlphaStack v4 is a systematic options trading engine built in C# (.NET 8) for Indian markets.

It supports:
- Multi-strategy signal generation (NIFTY + BANKNIFTY)
- Telegram approval gateway
- Paper execution with realistic simulation
- Automated exits (profit target / stop loss / expiry)
- Shadow trade logging (180 parameter variants per signal)
- Analytics + CSV export for strategy research

---

## Market Data

### Primary provider — Fyers API ✅
- NIFTY + BANKNIFTY spot
- India VIX
- Option chain LTP
- Historical OHLCV (indicators)

### Backup provider — Zerodha Kite Connect ✅
Used as failover if Fyers faces outages.

---

## Strategy Engine

All strategies extend `BaseSpreadEngine` which provides:
- VIX adaptive strike selection: `multiplier = 1.0 + (VIX/20)`, clamped 1.25–2.25x
- EMA50 trend confirmation filter (configurable)
- Shared indicator computation (EMA20, EMA50, ADR, ATR, Gap%)

### BullPutSpread ✅ LIVE (NIFTY)
- Bullish / low-volatility regime
- Mon / Wed / Fri entries, Tuesday expiry
- Telegram approval, paper fills, automated exits

### BearCallSpread ✅ LIVE (NIFTY)
- Bearish regime strategy
- Mon / Wed / Fri entries, Tuesday expiry

### BankNiftyBullPutSpread ✅ INTEGRATED
- BANKNIFTY, Wednesday expiry
- Strike interval 100pts, lot size 15
- Higher ADR multiplier base (+0.2x) for wider cushion

### BankNiftyBearCallSpread ✅ INTEGRATED
- BANKNIFTY, Wednesday expiry, bearish regime

### IronCondor ✅ BUILT
- NIFTY, Wednesday entry, Tuesday expiry
- Neutral regime — spot between EMA20 and EMA50
- 4 legs: BullPut + BearCall combined

---

## Telegram Commands

| Command | Description |
|---|---|
| /positions | Open positions |
| /pnl | Current P&L |
| /summary | Portfolio summary |
| /lasttrade | Latest closed trade |
| /export | Trade analytics CSV |
| /shadowexport | Shadow trade variants CSV + analysis |
| /tokenstatus | Fyers token freshness |

---

## Fyers Token Refresh

Daily flow:
1. 8:00 AM IST — Telegram sends login link
2. Tap link → login to Fyers in browser
3. Fyers redirects to `/api/fyers/callback`
4. Token auto-exchanged and stored in memory
5. Telegram confirms "✅ Token Refreshed"
6. 9:10 AM warning if token not yet refreshed

Manual fallback: `POST /api/fyers/token` in Swagger.

---

## Shadow Trade Logger

On every entry signal evaluation, 180 synthetic variants are logged:
- 5 ADR multipliers × 4 spread widths × 3 profit targets × 3 stop loss levels
- Same market context as real trade, different parameters
- Exit outcomes filled automatically by ShadowExitSimulatorJob
- Export via `/shadowexport` or `GET /api/analytics/shadow-export`

Use exported CSV in Excel to find optimal parameter combinations.

---

## Local Setup

### Prerequisites
- .NET 8 SDK
- PostgreSQL 15+

### 1. Database
```sql
CREATE DATABASE trading_platform;
CREATE USER trading_user WITH ENCRYPTED PASSWORD 'your_password';
GRANT ALL PRIVILEGES ON DATABASE trading_platform TO trading_user;
```

Run migrations in order:
```bash
psql -U trading_user -d trading_platform -f database/migrations/001_initial_schema.sql
# ... through 007_multi_user_prep.sql
```

### 2. Configuration
Fill in `appsettings.json`:
```json
{
  "Fyers": {
    "ClientId": "your_client_id",
    "SecretKey": "your_secret_key",
    "RedirectUri": "https://your-domain/api/fyers/callback",
    "AccessToken": "paste_daily_token_here"
  },
  "StrategySettings": {
    "Ema50FilterEnabled": true
  }
}
```

### 3. Run
```bash
cd src/AlphaStack.Api
dotnet run
```
Swagger UI: `http://localhost:5000`

---

## Completed (May 2026)

- ✅ Fyers integration (market data + option chain)
- ✅ NIFTY + BANKNIFTY instrument sync (config-driven)
- ✅ BaseSpreadEngine (shared base for all strategies)
- ✅ BullPutSpread + BearCallSpread (NIFTY)
- ✅ BankNiftyBullPut + BankNiftyBearCall
- ✅ IronCondor engine
- ✅ VIX adaptive strike selection (linear scaling)
- ✅ EMA50 trend confirmation filter
- ✅ Shadow Trade Logger (180 variants per signal)
- ✅ Fyers auto token refresh (Telegram + OAuth callback)
- ✅ Shadow CSV export + Telegram command
- ✅ Multi-user DB preparation (migration 007)
- ✅ Paper execution engine
- ✅ Live MTM tracking + max profit/loss
- ✅ Telegram approval workflow

---

## Roadmap

### Immediate
- Oracle Cloud hosting deploy

### Month 2
- Per-user Fyers token flow
- Friend onboarding
- Live order routing (FyersOrderService)

### Backlog
- Kelly-based position sizing
- Risk engine (3-layer)
- Backtest engine
- Composite entry variations
