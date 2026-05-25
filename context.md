# AlphaStack — Engineering Context (May 2026)
> Use this file at the start of every coding/debugging session.

## Project
AlphaStack — Semi-automated options trading platform for Indian index options.
Previously named TradingPlatform v4.

## Owner
Sourav — C# / ASP.NET Core. Solo dev. Paper trading active, targeting live in ~1 month.

---

## Current Architecture

### Tech Stack
- ASP.NET Core (.NET 8)
- PostgreSQL
- Clean Architecture (Domain / Application / Infrastructure / API)
- Telegram Bot API

### Market Data / Broker Setup

Primary:
- Fyers API (live market data + option chain + VIX + option LTP) ✅

Backup:
- Zerodha Kite Connect (code retained for failover / execution fallback) ✅

Removed / inactive:
- NSE provider ❌
- Dhan provider ❌

### Background Services

1. StrategyRunnerService
   - Evaluates entries at 9:20 IST weekdays

2. PnLTrackerService
   - Live MTM tracking every 5 min
   - Exit logic (profit target / stop loss / expiry)
   - Max profit/loss updates
   - ShadowExitSimulatorJob runs inside this cycle

3. InstrumentSyncService
   - Syncs NIFTY + FINNIFTY instruments at startup + 8 AM IST
   - Config-driven underlyings (appsettings InstrumentSync section)

4. StuckOrderMonitorService
   - Orphan order recovery

5. FyersTokenReminderService
   - Sends Telegram login link at 8:00 AM IST daily
   - Sends warning at 9:10 AM IST if token not refreshed
   - Marks token stale at midnight IST

---

## Strategies

### BullPutSpread ✅ LIVE (NIFTY)
- Mon / Wed / Fri entries, Tuesday expiry
- Telegram approval working
- Paper fills working
- Exit logic working
- Analytics working
- VIX adaptive strikes ✅
- EMA50 trend filter ✅

### BearCallSpread ✅ LIVE (NIFTY)
- Mon / Wed / Fri entries, Tuesday expiry
- Engine active, StrategyRunner evaluating
- VIX adaptive strikes ✅
- EMA50 trend filter ✅

### FINNIFTYBullPutSpread ✅ INTEGRATED
- Mon / Wed / Fri entries, Wednesday expiry
- Strike interval 100pts, lot size 15
- VIX adaptive strikes (FINNIFTY base +0.2x) ✅
- EMA50 trend filter ✅

### FINNIFTYBearCallSpread ✅ INTEGRATED
- Same as FINNIFTYBullPut, bearish regime
- VIX adaptive strikes ✅
- EMA50 trend filter ✅

### IronCondor ✅ BUILT
- NIFTY only, Wednesday entry, Tuesday expiry
- Neutral regime (spot between EMA20 and EMA50)
- VIX adaptive strikes ✅
- EMA50 neutral band gate ✅

---

## BaseSpreadEngine (shared base class)
All 5 strategy engines extend BaseSpreadEngine.
Shared logic:
- ComputeIndicatorsAsync (VIX, EMA20, EMA50, ADR, ATR, Gap%)
- ComputeAdrMultiplier(vix) — linear VIX adaptive: 1.0 + (vix/20), clamped 1.25-2.25x
- FINNIFTY override: 1.2 + (vix/20), clamped 1.5-2.5x
- EvaluateEntryGatesAsync — shared gate checks
- EMA50 filter (configurable via StrategySettings:Ema50FilterEnabled)
- BuildExitSignal, ShouldExitForExpiry
- Strike rounding via StrikeInterval (50 NIFTY, 100 FINNIFTY)

---

## Shadow Trade Logger ✅
- Logs 180 variants per signal (5 ADR × 4 widths × 3 targets × 3 SL)
- ShadowExitSimulatorJob fills exit outcomes in PnLTracker cycle
- Market hours gate — skips outside 9:15-15:30 IST and weekends
- DB table: shadow_trades (migration 006)

---

## Fyers Token Refresh ✅
- FyersTokenService singleton holds current token in memory
- Daily Telegram login link at 8 AM IST
- OAuth callback auto-exchanges auth_code → access_token
- No app restart needed after token refresh
- Manual fallback: POST /api/fyers/token
- Token status: GET /api/fyers/token-status or /tokenstatus Telegram command

---

## Telegram Commands
- /positions — open positions
- /pnl — current P&L
- /summary — portfolio summary
- /lasttrade — latest closed trade
- /export — trade analytics CSV
- /shadowexport — shadow trade variants CSV + parameter analysis
- /tokenstatus — Fyers token freshness check

Approval flow:
- Approve / Reject inline buttons working
- Callback data: "approve:{signalGroupId}" / "reject:{signalGroupId}"

---

## Analytics
Entry variations captured:
- MondayEntry, TuesdayEntry, WednesdayEntry, ThursdayEntry, FridayEntry

Fields captured:
- Strategy name, VIX, VixRegime, MarketRegime
- EMA20, EMA50, ATR, ADR, Gap%
- Strikes, Premium, SpreadWidth, AdrMultiplierUsed
- Holding time, Net/Gross PnL
- MaxMtmProfit, MaxMtmLoss, SpotTouchedShortStrike

CSV export working for both real trades and shadow variants.

---

## Multi-User Status
- Architecture is multi-user clean (all services loop all users/executions)
- DB columns added for per-user Fyers credentials (migration 007)
- Paper mode: shared Fyers token from config (FyersTokenService singleton)
- Live mode (Month 2): per-user FyersTokenService to be built
- Shared Telegram bot, per-user TelegramChatId routing
- Currently 1 active user

---

## Database Migrations
Location: /database/migrations/
- 001_initial_schema.sql
- 002_trades_table.sql
- 003_client_order_id.sql
- 004_trade_analytics.sql
- 005_add_spot_touched_short_strike.sql
- 006_add_shadow_trades.sql
- 007_multi_user_prep.sql

---

## Hosting
Current: Local machine + ngrok (Telegram webhook)
Planned: Oracle Cloud Always Free (ARM VM, Mumbai region) — PENDING SETUP

---

## Pending

### Immediate
1. Oracle Cloud hosting deploy (blocked on Oracle signup issue)

### Month 2 (after stable live run)
2. Per-user Fyers token flow (FyersTokenService → per-user)
3. Friend onboarding (simple API endpoint + strategy execution setup)
4. Live order routing (swap PaperOrderSimulator → FyersOrderService)

### Backlog
5. Composite entry variations
6. StrikeSelectionService (centralize strike logic)
7. Kelly-based position sizing
8. Risk engine (3-layer)
9. Backtest engine

---

## Key Design Decisions (locked)
| Decision | Rationale |
|---|---|
| Shared Telegram bot, per-user chat IDs | Sufficient for ≤3 users, simpler than per-user bots |
| Shadow trades fire-and-forget | Never blocks real trade execution |
| EMA50 filter configurable via appsettings | Can disable without redeploy for testing |
| VIX adaptive strikes linear formula | Smooth scaling, easy to analyse in CSV |
| FINNIFTY +0.2x base multiplier | Higher vol index needs wider cushion |
| Paper + Live isolated executions | Independent P&L from day one |
| PostgreSQL not SQLite | Concurrent writers, JSONB, TIMESTAMPTZ |
