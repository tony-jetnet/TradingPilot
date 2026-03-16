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

1. **Stage 1 — AI Rule Evaluation**: `StrategyRuleEvaluator` checks conditional rules from `strategy_rules.json` (generated nightly by Bedrock Sonnet 4.6). If a rule matches, emit signal immediately.
2. **Stage 2 — Weighted Scoring Fallback**: If no rule matches, compute composite score using 10 weighted indicators (weights from `model_config.json`, trained nightly by hill-climbing optimizer). Apply contextual filters (trend, VWAP, volume, RSI).

Both config files live at `D:\Third-Parties\WebullHook\` and are watched via `FileSystemWatcher` + periodic freshness check (1-min polling as failsafe).

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
9:30 PM ET  NightlyStrategyOptimizer.CleanupOldData — retention: L2 snapshots 3d, ticks 30d
```

The optimizer backfills TickSnapshots and TradingSignals from 1-minute bars before running AI analysis, so gaps from daytime app downtime don't affect training quality.

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
