# AlphaStack

Semi-automated options trading platform for Indian index options (NIFTY weekly).

---

## Overview

AlphaStack is a systematic options trading engine built in C# (.NET 8) for Indian markets.
Deployed on Oracle Cloud, accessible via Telegram for signal approval and position monitoring.

**Current status:** Paper trading active. Live trading targeting October 2026.

### Capabilities
- Multi-strategy signal generation with regime filtering
- Telegram approval gateway (semi-automated entries)
- Paper execution with realistic slippage simulation
- Automated exits (profit target / stop loss / expiry close)
- Shadow trade logger — 180 parameter variants per signal for research
- Analytics + CSV export for strategy optimisation

---

## Hosting (Production)

| Item | Value |
|---|---|
| Provider | Oracle Cloud Always Free |
| Shape | VM.Standard.E2.1.Micro (AMD, 1 OCPU, 1GB RAM) |
| OS | Ubuntu 22.04.5 LTS |
| Public IP | 155.248.247.85 (static) |
| Domain | alphastack.duckdns.org |
| HTTPS | Let's Encrypt (auto-renews) |

### Key paths on server
```
App:          /home/ubuntu/alphastack/
Config:       /home/ubuntu/alphastack/appsettings.json
Secrets:      /etc/alphastack.env
Migrations:   /home/ubuntu/migrations/
State:        /home/ubuntu/alphastack/data/strategy-runner-state.json
Token store:  /home/ubuntu/alphastack/data/fyers-token.json
Service:      /etc/systemd/system/alphastack.service
Nginx:        /etc/nginx/sites-available/alphastack
```

### Deploy (run from Mac)
```bash
~/deploy-alphastack.sh
```

### Service management
```bash
sudo systemctl start alphastack
sudo systemctl stop alphastack
sudo systemctl restart alphastack
journalctl -u alphastack -f          # live logs
journalctl -u alphastack -n 100      # last 100 lines
```

---

## Market Data

### Primary — Fyers API v3 ✅
- NIFTY spot, India VIX, option chain LTP, historical OHLCV

### Backup — Zerodha Kite Connect ✅
- Failover if Fyers is unavailable

---

## Strategies

All strategies extend `BaseSpreadEngine`:
- VIX adaptive strike selection: `1.0 + (VIX/20)`, clamped 1.25–2.25×
- EMA50 trend confirmation (configurable)
- Holiday detection via candle existence (no hardcoded dates)
- Synthetic instrument guard

| Strategy | Status | Underlying | Expiry | Notes |
|---|---|---|---|---|
| BullPutSpread | ✅ Active | NIFTY | Tuesday weekly | Bullish regime (spot > EMA20) |
| BearCallSpread | ✅ Active | NIFTY | Tuesday weekly | Bearish regime (spot < EMA20) |
| NiftyIronCondor | ✅ Active | NIFTY | Tuesday weekly | Neutral (spot between EMA20±0.5×ADR) |
| FinniftyBullPutSpread | ⏸ Disabled | FINNIFTY | Monthly only | Re-enable if NSE adds weekly |
| FinniftyBearCallSpread | ⏸ Disabled | FINNIFTY | Monthly only | Re-enable if NSE adds weekly |
| FinniftyIronCondor | ⏸ Disabled | FINNIFTY | Monthly only | Re-enable if NSE adds weekly |

### Key findings from shadow data (12+ trading days)
- **VIX > 18**: all parameter combinations lose — hard block gate pending
- **Wednesday/Friday entries**: structurally unprofitable (Wednesday -₹5,024 avg, Friday -₹2,391 avg)
- **Gap-down days**: BearCallSpread avg -₹2,483 vs gap-up +₹284 — direction gate pending
- **Regime filter**: valid regime = all combinations positive; wrong regime = all combinations negative

---

## Shadow Trade Logger

On every signal evaluation (including blocked ones), 180 synthetic variants are logged:
- **Matrix:** 5 ADR multipliers × 4 spread widths × 3 profit targets × 3 stop losses
- **IronCondor:** Put and Call wings paired via `shadow_group_id` (shared UUID per parameter combination)
- Exit outcomes simulated automatically by `ShadowExitSimulatorJob` every 5 minutes
- Export via `/shadowexport` Telegram command or `GET /api/analytics/shadow-export`

---

## Telegram

**Bot:** @ZerodhaTradingPlatform_Bot

| Command | Description |
|---|---|
| /positions | Open positions + unrealised P&L |
| /pnl | Today's P&L summary |
| /summary | Portfolio overview |
| /lasttrade | Latest closed trade |
| /export | Trade analytics CSV |
| /shadowexport | Shadow variants CSV (IST timestamps) |
| /tokenstatus | Fyers token freshness |

---

## Fyers Token (Daily)

1. **8:00 AM IST** — Telegram sends login link
2. Tap → login to Fyers in browser
3. Fyers redirects to `https://alphastack.duckdns.org/api/fyers/callback`
4. Token auto-exchanged, stored to `data/fyers-token.json`
5. Instrument sync + strategy evaluation triggered immediately
6. **9:25 AM** — scheduled evaluation (if token already refreshed)
7. **9:10 AM** — Telegram warning if token not yet refreshed

---

## Capital & Risk

| Parameter | Value |
|---|---|
| Total capital | ₹2,50,000 (in `user_profiles` DB table) |
| Max per trade | 35% = ₹87,500 |
| BullPutSpread margin | ₹50,000 (appsettings override) |
| BearCallSpread margin | ₹50,000 (appsettings override) |
| NiftyIronCondor margin | ₹80,000 (appsettings override) |

---

## Database

**Production:** `alphastack_prod` on localhost:5432

```bash
psql -U alphastack_user -d alphastack_prod
```

### Migrations
| File | Purpose |
|---|---|
| 001_initial_schema.sql | Base schema |
| 002_add_trade_aggregate.sql | Trades table |
| 003_add_client_order_id.sql | client_order_id column |
| 004_add_trade_analytics.sql | trade_analytics table |
| 005_add_spot_touched_short_strike.sql | Strike breach tracking |
| 006_add_shadow_trades.sql | shadow_trades table |
| 007_add_banknifty_strategies.sql | Strategy seeds |
| 008_add_iron_condor_strategy.sql | IronCondor seeds |
| 009_multi_user_prep.sql | Per-user Fyers credential columns |
| 010_add_market_regime_valid.sql | market_regime_valid on shadow_trades |
| 011_add_shadow_fees.sql | fees_rs, net_pnl_rs on shadow_trades |
| 012_add_shadow_group_id.sql | shadow_group_id for IC wing pairing |
| 013_add_lot_size.sql | lot_size on trade_analytics + shadow_trades |

---

## Local Setup

### Prerequisites
- .NET 8 SDK
- PostgreSQL 15+

### 1. Database
```sql
CREATE DATABASE alphastack_dev;
CREATE USER alphastack_user WITH ENCRYPTED PASSWORD 'your_password';
GRANT ALL PRIVILEGES ON DATABASE alphastack_dev TO alphastack_user;
```

Run migrations in order from `database/migrations/`.

### 2. Secrets
Copy to `/etc/alphastack.env` (never commit to git):
```env
ConnectionStrings__DefaultConnection=Host=localhost;Port=5432;Database=alphastack_dev;Username=alphastack_user;Password=your_password
Fyers__ClientId=your_client_id
Fyers__SecretKey=your_secret_key
Telegram__BotToken=your_bot_token
```

### 3. appsettings.json
```json
{
  "Fyers": {
    "RedirectUri": "https://your-domain/api/fyers/callback"
  },
  "StrategySettings": {
    "Ema50FilterEnabled": true,
    "BullPutSpread":   { "EstimatedMarginOverride": 50000 },
    "BearCallSpread":  { "EstimatedMarginOverride": 50000 },
    "NiftyIronCondor": { "EstimatedMarginOverride": 80000 }
  }
}
```

### 4. Run
```bash
cd src/AlphaStack.Api
dotnet run
```
Swagger UI: `http://localhost:5000`

---

## Roadmap

### Now
- ✅ Paper trading live on Oracle Cloud
- ✅ Shadow data collection (12+ trading days)
- 🔧 VIX hard block gate (VIX > 18)
- 🔧 Entry day gate (block Wednesday + Friday)
- 🔧 Gap direction gate (skip BearCallSpread on gap-down days)
- 🔧 Telegram commands fix

### Month 2 (before friend goes live)
- Per-user Fyers token flow
- Friend onboarding endpoint
- Live and paper trade SOC.
- Live order routing (FyersOrderService)

### Phase 3
- Backtest engine
- Web UI / dashboard
- Kelly-based position sizing
- SENSEX integration (BFO exchange)
