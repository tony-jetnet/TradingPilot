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
2. **Stage 2 — Swin + Weighted Scoring Blend**: If no rule matches, always compute the weighted score from **6 L2/tick indicators** (OBI, WOBI, PressureRoc, Spread, LargeOrder, TickMomentum). If the Swin vision model (ONNX) is confident (≥0.40), **blend** Swin score with weighted score: `composite = swinScore × confidence + weightedScore × (1-confidence)`. If Swin unavailable/low-confidence, use weighted score alone. Then apply contextual filters (trend, VWAP, volume, RSI) as post-scoring multipliers. This prevents Swin from overriding indicator disagreement.

**No double penalization**: Trend, VWAP, Volume, and RSI are handled ONLY by contextual filters (weight=0 in the scoring). They are NOT included as weighted indicators in the composite score. This prevents the same signal from being penalized twice.

Both config files live at `D:\Third-Parties\WebullHook\` and are watched via `FileSystemWatcher` + periodic freshness check (1-min polling as failsafe).

### Contextual Filters (applied to BOTH Stage 1 and Stage 2)

Applied in sequence after the base score is computed:
1. **Trend filter**: Signal against dominant EMA trend → score × 0.5 (sets `trendFilterApplied = true`)
2. **VWAP filter**: Signal against VWAP position → score × 0.7
3. **Volume boost**: High volume AND trend not against signal → score × 1.3 (directional — only boosts when `!trendFilterApplied`)
4. **RSI filter**: Graduated — RSI 75-80 → ×0.70, RSI 80-85 → ×0.50, RSI 85+ → ×0.30 (symmetric for oversold)
5. **Floor protection**: No single filter chain can reduce score below **30%** of pre-filter value (**applies to BOTH stages** — Stage 1 rules now have floor protection too)
6. **Hourly multiplier**: Per-hour learned multiplier from model config
7. **Staleness guards**: Skip signal if L2 data >30s old or bar indicators >2min old

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
9:00 PM ET  NightlyStrategyOptimizer.OptimizeAsync    — verify signal outcomes + backfill gaps + Bedrock AI rules
9:30 PM ET  NightlyLocalTrainer.TrainAsync            — hill-climbing weight optimization (needs verified outcomes from above)
10:00 PM ET NightlyStrategyOptimizer.CleanupOldDataAsync — retention: L2 snapshots 20d, ticks 30d
```

**Order matters**: Optimizer runs first because `VerifySignalOutcomesAsync()` fills PriceAfter5Min/15Min/30Min columns that the trainer needs.

The optimizer backfills TickSnapshots and TradingSignals from 1-minute bars before running AI analysis. **Backfilled signals are excluded from both trainer and Bedrock CSV** (different score distribution from live L2 signals).

### Bedrock AI Rule Generation (NightlyStrategyOptimizer)

- **Rule preservation**: Before calling Bedrock, `EvaluateExistingRulesAsync()` evaluates current rules against last 5 days of live signal data. Rules with ≥5 matches AND positive P&L (using PriceAfter5Min, spread-adjusted) survive. Surviving rules are kept as-is; Bedrock only fills vacated slots (max 5 rules/symbol total). Surviving rules are passed to Bedrock as context so it generates complementary rules, not duplicates.
- **HoldSeconds**: Prompt instructs 60-600s (1-10 min, matching L2 signal decay). Validator enforces same range.
- **MaxDailyTrades**: Capped at 5 per symbol in rule output.
- **Local backtesting**: After Bedrock generates rules, each rule is backtested on the newest 25% of data. Only rules with ≥5 matches AND positive P&L survive.
- **Backfilled signals excluded** from the CSV sent to Bedrock.
- **Data quality filters**: Market hours only (9:30 AM - 4:00 PM ET). Cold-cache signals excluded (OBI=WOBI=TickMomentum=0).
- **Skip guard**: 2-hour cooldown (prevents double-runs on manual trigger), not daily skip.

### Nightly Trainer Details (NightlyLocalTrainer)

- **Walk-forward**: 75% train (oldest) / 25% validation (newest), time-ordered
- **Multi-start hill-climbing**: 3 restarts × 300 iterations each (900 total), timestamp-based random seeds (different each night). Perturbs one of the **6 L2/tick weights** (indices 0-5) by ±0.05, keeps if P&L improves. Best result across all restarts is kept.
- **Multi-horizon outcome**: Training labels use weighted blend: `0.20×PriceAfter5Min + 0.40×PriceAfter15Min + 0.40×PriceAfter30Min` (approximates actual exit time distribution instead of fixed 30-min horizon)
- **Overfit guard (strengthened)**: Falls back to defaults if: (a) optimized weights lose on validation AND defaults win, OR (b) defaults outperform by 2×+, OR (c) validation P&L < 15% of training P&L (train/val divergence)
- **Threshold validation**: Optimized thresholds are validated on held-out set; reverts to defaults if they lose on validation
- **Hour/direction gating**: Uses expected value (EV = avgWin×winRate - avgLoss×lossRate) instead of raw win rate. Minimum 10 samples per hour.
- **Training data filters**: Only signals with |Score| ≥ 0.20 (tradeable signals). Excludes backfilled bar-derived signals (`Reason NOT LIKE '%BACKFILL%'`). Market hours only (9:30 AM - 4:00 PM ET). Excludes cold-cache signals (OBI=0 AND WOBI=0 AND TickMomentum=0). Requires at least PriceAfter5Min (rows with only 1-min outcome are excluded — they fell into data gaps).
- **Weights 6-9 (Trend, VWAP, Volume, RSI)**: Fixed at 0 — contextual filters handle these. Optimizer only perturbs weights 0-5.
- **Hold time optimization (2026-03-25)**: Tests candidates [300, 600, 900, 1200, 1800] seconds per ticker. For each, computes P&L using the closest price outcome columns (PriceAfter5/15/30Min with interpolation). Walk-forward validated: falls back to 1200s default if best candidate loses on validation. Default changed from 3600s to 1200s (20 min) to match the ~19 min weighted training horizon (`0.20×5min + 0.40×15min + 0.40×30min`). The old 3600s default caused all Stage 2 trades to exit via TIME+WEAK because L2 scores naturally decay over 60 min.

### Position Management (PositionMonitor)

**Entry gating** (PaperTradingExecutor.OnSignalAsync):
- Auth, position limit, daily P&L hard stops (stop if day P&L ≤ -$500 or ≥ +$500), rate limit (5min/300s cooldown)
- Spread regime filter: reject if spread ≥ 90th percentile
- Momentum check: find L2 snapshot closest to 30s ago (15-60s window), require price-relative momentum alignment ≥ 0.05% of mid price
- ATR-based position sizing: $25K base scaled by ATR (high vol → smaller position), further scaled by signal strength (|score|/0.40, clamped 0.50-1.0)
- Tiered limit orders: strong signals (score ≥ 0.40) use midprice; moderate signals use bid+10% spread offset
- Entry order timeout: 90 seconds (stale L2 signals expire quickly)
- Rule entry threshold: 0.35 (same as Stage 2, since raw confidence already passed 0.55 gate in StrategyRuleEvaluator)
- **Opposing signal exit**: If we hold a position and get a strong opposing signal (|score| ≥ 0.40), exit immediately. Threshold 0.40 matches "strong signal" throughout the codebase, prevents whipsaw from moderate signals. Event-driven (fires on signal arrival, not 5s poll).

**Exit checks** (evaluated every 5s in priority order):

**Key principle — profit-side thresholds are decoupled from stop distance (2026-03-25 fix)**:
The stop loss uses `Max(ruleStop, 2.0×ATR, entrySpread)` which is correct for max acceptable loss. But profit-protection exits (trailing, breakeven, profit target) use **price-percentage-based thresholds** instead of multiples of stop distance. This is because 2×ATR can be 1-2% of price — unreachable for typical day trades that move 0.1-0.5% in 5-60 minutes.

| Threshold | Formula | AMD $218 (ATR $2) | RIVN $15.66 |
|-----------|---------|-------------------|-------------|
| Trailing activation | `Max(entryPrice × 0.0015, entrySpread × 2)` | $0.33 (0.15%) | ~$0.05 (spread floor) |
| Breakeven activation | `trailingActivation × 2.0` | $0.66 | ~$0.10 |
| Profit target | `Max(entryPrice × 0.010, effectiveStopLoss × 1.5)` | $6.00 | $2.25 |
| Regime exit threshold | `effectiveStopLoss × 0.40` | $1.60 | $0.60 |

0. **Profit Target**: Exit when profit ≥ `Max(entryPrice × 1.0%, effectiveStopLoss × 1.5)`. AMD: $6.00. The 1.5× stop floor ensures minimum 1.5:1 risk/reward. Previously was 3.0× stop ($12 for AMD — unreachable intraday).
1. **VWAP Cross** (grace: holdTime×0.50, max 15min): **tightens trailing stop to 30% giveback** (NOT a hard exit)
2. **EMA Trend Reversal** (grace: holdTime×0.33, max 10min): **tightens trailing stop to 30% giveback** (NOT a hard exit)
3. **RSI Extreme** (grace: holdTime×0.17, max 5min): **graduated tightening** — RSI 75-80→40%, RSI 80+→25% (NOT a hard exit)
3.5. **Regime Exit** (NEW): When VWAP/EMA/RSI tighteners fire AND trailing is NOT active (profit below activation) AND loss exceeds 40% of stop distance → exit. This makes VWAP/EMA/RSI indicators effective even without the trailing stop. AMD: exits at $1.60 loss vs $4.00 full stop. NOT a hard exit from VWAP/EMA/RSI directly — requires: indicator adverse + trailing not active + losing 40% of stop.
4. **Stop Loss**: Max(rule stop, **2.0×ATR**, entry spread) — volatility-adaptive, unchanged
5. **Breakeven Stop**: Once peak profit > `trailingActivation × 2.0` (AMD: $0.66), exit if position falls below **-0.25× stop** (buffer prevents single-tick exits). Previously required 2.0× stop distance ($8.00 for AMD — unreachable).
6. **Trailing Stop**: Activates when peak profit > `Max(entryPrice × 0.15%, entrySpread × 2)`. AMD: $0.33. Anti-wick filtered (10s persistence). **Inverted** confidence-scaled giveback: `0.35 + confidence×0.25` (higher confidence → MORE room, 0.85 conf → 56%). Stage 2: 50% giveback. VWAP/EMA/RSI tightening overrides applied here. Previously required peakProfit > effectiveStopLoss ($4 for AMD — unreachable).
7. **Time Gate**: Adaptive — past hold time check score strength; hard cap at 2× hold time. **Score=0 handling**: ComputeCurrentScore returns 0 on data gaps (cold cache, no snapshots). Score of exactly 0.000 is never natural (6 blended indicators). If score=0 AND profitable → hold (don't exit on stale data). If score=0 AND losing → exit as TIME+NOSIGNAL.
8. **Exit order escalation**: If exit order unfilled after 30s, cancel and resubmit with aggressive price (cross spread by 0.05%)

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

### Data Quality Guards

- **ImbalanceVelocity**: Uses closest-sample interpolation (finds OBI sample nearest to 30s ago, normalizes by actual time delta). Avoids stuck-at-zero from rigid 25-35s window.
- **SpreadPercentile**: Time-based 5-minute window (not count-based). Queue size 600. Returns 0.50 if <10 samples.
- **Bar history**: 60 bars loaded (up from 30) for proper EMA20/RSI14 convergence.
- **VWAP**: Filtered to today's ET trading session only (prevents cross-day contamination).
- **BarIndicatorCache.LastRefreshTime**: Tracked per ticker. Analyzer skips signal generation if bar indicators are >2 minutes stale.

## Stabilization Plan (2026-03-23, updated 2026-03-25)

**AUTHORITATIVE REFERENCE**: See `docs/final/` for the complete stabilization plan that was implemented. All parameter decisions are final — do NOT re-derive or change without quantitative evidence. Key decisions locked:

| Parameter | Value | Rationale |
|-----------|-------|-----------|
| ATR stop multiplier | 2.0× | 1.5× inside normal candle noise, 2.5× too wide |
| VWAP/EMA/RSI exits | Trailing tighteners (NOT hard exits) | Hard exits killed winners during oscillation |
| Floor protection | 30%, both stages | 50% too generous, 0% too brittle |
| HoldSeconds range (rules) | 60-600 | L2 signals decay in minutes, not hours |
| Training outcome | Multi-horizon weighted (0.20/0.40/0.40) | Actual exits happen at 5-15 min, not 30 min |
| Trailing giveback (rules) | 0.35 + conf×0.25 (inverted) | High confidence gets MORE room |
| Entry timeout | 90 seconds | L2 signal stale after 30s |
| Rate limit | 300 seconds | Balance between whipsaw prevention and re-entry |
| Daily P&L stops | -$500 loss / +$500 profit → stop for day | Outcome-based, not count-based |
| Backfills in training | Excluded | Bar-derived scores corrupt L2 weight optimization |

### Exit Strategy Overhaul (2026-03-25)

**Problem solved**: 8/10 trades exiting via TIME+WEAK because all profit-side exit thresholds scaled off `effectiveStopLoss` (2×ATR ≈ 1-2% of price) which was unreachable for typical 0.1-0.5% day trade moves. VWAP/EMA/RSI tighteners were dead code (set `trailingOverride` but trailing never activated). Score=0 from data gaps triggered false TIME+WEAK exits. Opposing signals were documented but never implemented.

**New locked parameters** (do NOT change without quantitative evidence):

| Parameter | Value | Rationale |
|-----------|-------|-----------|
| OptimalHoldSeconds default | **1200s** (20 min) | Matches weighted training horizon: 0.20×5 + 0.40×15 + 0.40×30 = ~19 min. Old 3600s was 3× longer than what model trained on. Nightly trainer now optimizes per-ticker from [300,600,900,1200,1800]. |
| Trailing activation | **0.15% of entry price** (floored at 2× entry spread) | AMD $218 → $0.33. Old was effectiveStopLoss ($4 for AMD = 1.8% — unreachable). 0.15% is realistic for 5-60 min day trades. Floor at 2× spread prevents activation from noise inside bid-ask. |
| Breakeven activation | **2× trailing activation** | AMD → $0.66. Old was 2× stop ($8 — unreachable). Position must reach meaningful profit before protecting at breakeven. |
| Profit target | **Max(1.0% of entry, 1.5× stop)** | AMD → Max($2.18, $6) = $6. Old was 3× stop ($12 — impossible intraday). 1.5× stop floor guarantees minimum risk/reward ratio. |
| Regime exit threshold | **40% of stop distance** | AMD → $1.60. When VWAP/EMA/RSI say "wrong direction" AND trailing not active AND losing 40% of stop → exit early. NOT a hard exit from indicators — requires losing money + indicator confirmation + 40% stop threshold. |
| Opposing signal threshold | **0.40** (strong signal) | Matches "strong" classification throughout codebase. Only exits on high-conviction reversals, prevents whipsaw from moderate signals. |
| Score=0 handling | **Unknown, not weak** | ComputeCurrentScore returns exactly 0.000 on data gaps (never natural from 6 blended indicators). Profitable positions survive data gaps. Losing positions with no signal exit as TIME+NOSIGNAL. |

## Swin Vision Model (ml/ directory)

### Overview

Swin-Tiny (28M params) fine-tuned on L2 order book heatmaps (224×224 RGB). Each image encodes ~300 consecutive L2 snapshots: R=ask sizes, B=bid sizes, G=midprice+spread. Classifies as UP/FLAT/DOWN (5-min horizon, ±30 bps threshold). Integrated into Stage 2 scoring via ONNX inference in the Blazor app.

### Pipeline (`retrain.py` — nightly after market close)

```
export_data.py --days 20  →  render_heatmap.py  →  train.py  →  export_onnx.py
(L2 from DB)               (numpy heatmaps)      (fine-tune)   (swin_trading.onnx)
```

### Key Design Decisions

- **20-day training window**: DB retains L2 snapshots for 20 days. This is the optimal window — older data reflects stale market regimes (different volatility, spreads, market makers). No archiving needed; each nightly export pulls fresh 20-day window directly from DB.
- **No archive/merge**: Previously archived and merged daily exports, causing massive data duplication (each 20-day export overlaps 19 days with the previous). Removed — the 20-day export IS the training set.
- **~28K samples is sufficient**: Fine-tuning (not training from scratch) — pretrained ImageNet features transfer to heatmaps. Only top 2 stages unfrozen (~7-8M params). More data adds stale regime noise.
- **Memory-mapped Dataset**: `images.npy` loaded via `np.load(mmap_mode='r')`. Dataset stores file path + index array (not the array itself). Each DataLoader worker opens its own mmap handle lazily. This avoids Windows `spawn` pipe size limits that crash at ~2 GB+.
- **Mixed precision (AMP)**: `torch.amp.autocast` + `GradScaler` for ~2x GPU speedup with negligible accuracy impact.

### Training Config (ml/config.py)

| Setting | Value | Notes |
|---------|-------|-------|
| STRIDE_SNAPSHOTS | 100 | ~67% overlap between windows (was 30 = 90% overlap, too correlated) |
| UP/DOWN_THRESHOLD | ±0.003 (30 bps) | Must exceed spread + slippage to be profitable |
| BATCH_SIZE | 64 | |
| LEARNING_RATE | 1e-4 | Phase 1 and Phase 2 base LR |
| EPOCHS_HEAD | 5 | Phase 1: frozen backbone |
| EPOCHS_FINETUNE | 25 | Phase 2: top 2 stages unfrozen, warmup + cosine decay |
| EARLY_STOPPING_PATIENCE | 10 | |
| SEED | 42 | Reproducibility |

### Data Quality Guards (render_heatmap.py)

- **Market hours filter**: Only snapshots 9:30 AM - 4:00 PM ET used for training. Pre/post-market have different spread/volume characteristics.
- **Gap detection**: Windows with any consecutive snapshot gap > 5 seconds are skipped. Prevents cross-gap heatmaps that span hours of real time while looking continuous.
- **Window time span check**: Main window must span 150-600 seconds of real time (expected ~300s for 300 snapshots). Rejects windows with abnormal snapshot frequency.
- **Future window validation**: Label window must span 50%-150% of HORIZON_SECONDS. Prevents labels that measure wrong time horizons due to gaps.
- **Skip statistics**: Logged after each symbol showing how many windows were rejected by each filter.

### Class Imbalance

Typical distribution: ~85% FLAT / ~7.5% DOWN / ~7.5% UP. Handled by `WeightedRandomSampler` + `CrossEntropyLoss(weight=class_weights)`. The real bottleneck is minority class count (~2K UP/DOWN), not total samples.

## Reference

See `SYSTEM_REFERENCE.md` for complete database schema (10 tables), all Hangfire jobs with schedules, data retention policies, and write volumes.
