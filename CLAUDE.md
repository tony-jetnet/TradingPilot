# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
# Build entire solution
dotnet build TradingPilot.slnx

# Run the Blazor server (main entry point — includes Hangfire, MQTT, trading engine)
cd src/TradingPilot.Blazor && dotnet run
# Blazor: https://localhost:44324
# Hangfire Dashboard: https://localhost:44324/hangfire
# Swagger: https://localhost:44324/swagger

# Run tests
dotnet test TradingPilot.slnx
dotnet test test/TradingPilot.Application.Tests  # single project

# Database migrations
cd src/TradingPilot.DbMigrator && dotnet run

# EF Core migration (generate new)
dotnet ef migrations add <Name> --project src/TradingPilot.EntityFrameworkCore --startup-project src/TradingPilot.Blazor
```

**Note:** The Blazor process locks DLLs. If build fails with MSB3027 file-lock errors, stop the running app first.

## Architecture

ABP Framework 10.1 app with DDD layered architecture. .NET 10.0, PostgreSQL, Redis.

### Two-Stage Signal Generation

Real-time L2 order book data flows through a two-stage signal pipeline in `MarketMicrostructureAnalyzer.AnalyzeSnapshot()`:

1. **Stage 1 — AI Rule Evaluation**: `StrategyRuleEvaluator` checks conditional rules from `strategy_rules.json` (generated nightly by Bedrock Sonnet 4.6). If a rule matches, apply contextual filters (trend, VWAP, volume, RSI) to the rule's confidence score. Reject if filtered score drops below 0.20. Also checks hourly enablement from model config and live rule performance tracking.
2. **Stage 2 — Weighted Scoring Fallback**: If no rule matches, compute composite score using **6 L2/tick indicators** (OBI, WOBI, PressureRoc, Spread, LargeOrder, TickMomentum). Weights from `model_config.json` (trained nightly). Then apply contextual filters (trend, VWAP, volume, RSI) as post-scoring multipliers.

**No double penalization**: Trend, VWAP, Volume, and RSI are handled ONLY by contextual filters (weight=0 in the scoring). They are NOT included as weighted indicators in the composite score. This prevents the same signal from being penalized twice.

Both config files live at `D:\Third-Parties\WebullHook\` and are watched via `FileSystemWatcher` + periodic freshness check (1-min polling as failsafe).

### Contextual Filters (applied to BOTH Stage 1 and Stage 2)

Applied in sequence after the base score is computed:
1. **Trend filter**: Signal against dominant EMA trend → score × 0.5
2. **VWAP filter**: Signal against VWAP position → score × 0.7
3. **Volume boost**: High volume → score × 1.3
4. **RSI filter**: Signal into overbought/oversold → score × 0.5
5. **Floor protection**: No single filter chain can reduce score below 50% of pre-filter value
6. **Hourly multiplier**: Per-hour learned multiplier from model config

### Weighted Scoring Indicators (6 indicators, sum to 1.0)

| Indicator | Default Weight | Range | Source |
|-----------|---------------|-------|--------|
| OBI (smoothed) | 0.25 | [-1, +1] | L2BookCache (30-snapshot average) |
| WOBI (weighted) | 0.25 | [-1, +1] | L2 snapshot (inverse-distance weighted) |
| PressureRoc | 0.15 | [-1, +1] | Short vs long imbalance averages |
| SpreadSignal | 0.10 | [-1, +1] | Spread percentile (tight=bullish) |
| LargeOrderSignal | 0.10 | [-1, +1] | Size spikes > 3x average |
| TickMomentum | 0.15 | [-1, +1] | Uptick/downtick ratio from TickDataCache |

### VWAP Score Formula

`deviation = (currentPrice - VWAP) / VWAP`, then `Clamp(deviation × 10, -1, 1)`. A 0.1% deviation → 0.10 score, 0.5% → 0.50, 1.0% → saturates at ±1.0. Divides by VWAP (not price) for symmetry.

### Live Rule Performance Tracking

`StrategyRuleEvaluator` tracks per-rule win/loss/P&L from closed trades in real-time. Rules with negative total P&L after ≥3 trades are auto-disabled for the rest of the trading day. Performance resets when new `strategy_rules.json` is loaded (nightly).

### Real-Time Data Flow

```
Webull MQTT (via injected hook DLL)
  -> MqttMessageProcessor (decodes protobuf/JSON)
     -> L2 depth: SymbolBookSnapshots DB + L2BookCache + TickDataCache.UpdateL2Features()
                  -> MarketMicrostructureAnalyzer -> TradingSignals DB
                     -> PaperTradingExecutor -> PaperTrades DB + Webull paper API
     -> Quotes: TickDataCache + BarIndicatorService refresh (30s) + TickSnapshots DB (10s)
     -> Ticks: TickDataCache (uptick/downtick counts, momentum)
```

### Nightly Job Sequence (after market close, weekdays)

```
9:00 PM ET  NightlyModelTrainer.TrainAsync(20)     — hill-climbing weight optimization
9:15 PM ET  NightlyStrategyOptimizer.OptimizeAsync  — backfill gaps + Bedrock AI per symbol
9:30 PM ET  NightlyStrategyOptimizer.CleanupOldData — retention: L2 snapshots 20d, ticks 30d
```

The optimizer backfills TickSnapshots and TradingSignals from 1-minute bars before running AI analysis, so gaps from daytime app downtime don't affect training quality.

### Nightly Trainer Details (NightlyLocalTrainer)

- **Walk-forward**: 75% train (oldest) / 25% validation (newest), time-ordered
- **Hill-climbing**: 100 iterations, perturbs one of the **6 L2/tick weights** (indices 0-5) by ±0.05, keeps if P&L improves
- **Overfit guard**: If optimized weights lose money on validation AND defaults do better, falls back to defaults
- **Training data**: All 10 indicators are loaded from TradingSignalRecord (OBI, WOBI, PressureRoc, SpreadSignal, LargeOrderSignal, TickMomentum, Ema9, Ema20, Rsi14, Vwap, VolumeRatio) — derived scores computed from raw values matching live formulas
- **Weights 6-9 (Trend, VWAP, Volume, RSI)**: Fixed at 0 — contextual filters handle these. Optimizer only perturbs weights 0-5.

### Position Management (PositionMonitor)

**Entry gating** (PaperTradingExecutor.OnSignalAsync):
- Auth, position limit, daily loss limit, rate limit (10min cooldown), daily trade count
- Spread regime filter: reject if spread ≥ 90th percentile
- Momentum check: find L2 snapshot closest to 30s ago (15-60s window), require price-relative momentum alignment
- ATR-based position sizing: $25K base scaled by ATR (high vol → smaller position)
- Passive limit orders: bid+10% spread offset for buys (saves spread, acts as signal quality filter)

**Exit checks** (evaluated every 5s in priority order):
1. **VWAP Cross** (15min grace): price crosses VWAP against position
2. **EMA Trend Reversal** (10min grace): EMA9 crosses EMA20 against position
3. **RSI Extreme** (5min grace): RSI > 75 (long) or < 25 (short)
4. **Stop Loss**: Max(rule stop, 1.5×ATR, entry spread) — volatility-adaptive
5. **Breakeven Stop**: Once peak profit > **1.5×** stop distance, exit if position turns negative
6. **Trailing Stop**: Anti-wick filtered (10s persistence), confidence-scaled giveback (higher confidence → tighter trail)
7. **Time Gate**: Adaptive — past hold time check score strength; hard cap at 2× hold time

### Singleton Caches (in-memory, registered in BlazorModule)

| Cache | Purpose |
|-------|---------|
| `L2BookCache` | Rolling 720 L2 snapshots per ticker |
| `TickDataCache` | Real-time tick/quote data + L2-derived features (7 fields) |
| `BarIndicatorCache` | EMA9/20, RSI14, VWAP, VolumeRatio (refreshed every 30s) |
| `StrategyRuleEvaluator` | AI-generated conditional rules (loaded from file) |
| `SignalStore` | Recent signals for display |

### Key Domain Types

- `SymbolBookSnapshot` — L2 order book (bid/ask arrays as JSONB, spread, midprice, imbalance)
- `TickSnapshot` — 10-second snapshot with 17 indicators + 7 L2-derived features
- `TradingSignalRecord` — Buy/sell signal with indicators, verified with PriceAfter1Min/5Min/15Min/30Min
- `ModelConfig` / `TickerModelConfig` — Learned weights + thresholds from hill-climbing
- `StrategyConfig` / `StrategyRule` — AI-discovered conditional rules with per-rule hold time, stop loss

## Important Patterns

### Auth Header Resolution

All Webull API jobs use a `ResolveAuthHeader()` pattern: try in-memory `WebullHookAppService.CapturedAuthHeader` first, fall back to reading `D:\Third-Parties\WebullHook\auth_header.json` from disk. This ensures jobs work even if the MQTT hook hasn't intercepted a request yet.

### Database Access in Singletons

Singleton services (MqttMessageProcessor, PaperTradingExecutor) use `IServiceScopeFactory` to create scoped DB access:
```csharp
using var scope = _scopeFactory.CreateScope();
var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
var repo = scope.ServiceProvider.GetRequiredService<IRepository<Entity, Guid>>();
using var uow = uowManager.Begin();
// ... operations
await uow.CompleteAsync();
```

### Timestamps

All timestamps stored in PostgreSQL as `timestamp without time zone` in **UTC**. When converting to ET in SQL, use the double `AT TIME ZONE` pattern:
```sql
-- CORRECT: declare UTC first, then convert to ET
(ts."Timestamp" AT TIME ZONE 'UTC') AT TIME ZONE 'America/New_York'

-- WRONG: PostgreSQL assumes the value IS in ET
ts."Timestamp" AT TIME ZONE 'America/New_York'
```

In C#, use `TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, eastern)` (already correct throughout).

### Hangfire Job Attributes

Background jobs use `[DisableConcurrentExecution(seconds)]` to prevent overlapping runs and `[AutomaticRetry(Attempts = N)]` for retry policy. Jobs that need auth use `ResolveAuthHeader()` and return early (not throw) if unavailable.

## External Dependencies

- **PostgreSQL** — `localhost:5432/TradingPilot` (connection string in appsettings.json)
- **Redis** — `localhost` database 10 (Hangfire storage + ABP cache)
- **AWS Bedrock** — `us-west-2`, model `anthropic.claude-sonnet-4-6-20250514-v1:0` (nightly strategy optimization)
- **Webull Desktop** — MQTT hook DLL injected via `ProcessInjector` for real-time data capture

## Reference

See `SYSTEM_REFERENCE.md` for complete database schema (10 tables), all Hangfire jobs with schedules, data retention policies, and write volumes.
