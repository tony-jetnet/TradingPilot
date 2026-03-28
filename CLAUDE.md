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

# Python ML — L2 Swin model (entry timing)
cd ml/l2 && python retrain.py

# Python ML — Day trading model (setup detection + backtesting)
cd ml/daytrading && python export_bars.py --days 60
cd ml/daytrading && python compute_features.py
cd ml/daytrading && python train.py
cd ml/daytrading && python backtest.py
cd ml/daytrading && python evaluate.py
```

**Note:** The Blazor process locks DLLs. If build fails with MSB3027 file-lock errors, stop the running app first.

## Architecture Overview

ABP Framework 10.1 app with DDD layered architecture. .NET 10.0, PostgreSQL, Redis.

**Trading style: Intraday day trading** on US equities. Hold 30 min to 4 hours. Close all positions before EOD. No overnight. The system uses bar-based setups as the primary signal, L2 order book for entry timing, and news/fundamentals for context.

**Symbol management**: 50 symbols watched permanently (all receive data, all have trained models). Pre-market scanner ranks and selects top 10 for active trading each day.

### Three-Layer Signal Architecture

```
Layer 1: SETUP DETECTION (bar-based, every 5-min bar close)
  "Is there a trade worth taking?"
  → Scans 1m/5m/15m bars for day-trade setups
  → Produces: SetupResult (type, direction, strength, entry zone, stop, target)
  → Fully backtest-able on historical SymbolBars data

Layer 2: L2 TIMING (real-time, on every L2 update while setup active)
  "Is now the right moment to enter?"
  → Existing 6 L2 indicators + Swin vision model
  → Confirms setup with microstructure pressure
  → Only works live (no historical L2)

Layer 3: CONTEXT SCORING (news + fundamentals + capital flow)
  "Should we take this trade?"
  → News sentiment and catalyst detection
  → Capital flow direction (institutional money)
  → Earnings proximity, short float, time of day

Composite = SetupScore × 0.50 + TimingScore × 0.30 + ContextScore × 0.20
Then contextual filters (trend, VWAP, volume, RSI) applied as multipliers
Floor protection: 30% (no filter chain can reduce score below 30% of pre-filter value)
```

### Setup Types (SetupDetector)

| Setup | Entry Condition | Stop Level | Target | Invalidation |
|-------|----------------|------------|--------|--------------|
| **TREND_FOLLOW** | EMA9 > EMA20 > EMA50 on 5m, all rising, price pulls back to EMA20 zone (within 0.3×ATR) | Below EMA50 or recent swing low | 2-3× ATR above entry | EMA20 crosses below EMA50 on 5m |
| **VWAP_BOUNCE** | Price touches VWAP from above, bounces with VolumeRatio > 1.5, RSI 40-60 | Below VWAP by 0.3% | Prior high or VWAP + 2× pullback distance | Price closes below VWAP on 5m bar |
| **BREAKOUT** | Price breaks above 15m consolidation range (>30 min range) with volume > 2× avg | Mid-point of consolidation range | Range height projected above breakout | Price closes back inside range |
| **REVERSAL** | RSI divergence (price new low, RSI higher low) + volume + key support level | Below the swing low that formed divergence | EMA20 on 15m (first target), prior swing high | Price makes new low below divergence low |

Each setup has: direction (BUY/SELL), strength [0-1], entry zone (price range), stop level, target level, invalidation condition, expiry (30-60 min shelf life).

### News Integration (Three Roles)

**Role 1 — Catalyst Detection** (pre-market + first 30 min):
- Scan news from last 12 hours for each symbol
- Catalyst types: EARNINGS, ANALYST, REGULATORY, SECTOR, CORPORATE
- Catalyst stocks get +0.15 strength boost on BREAKOUT setups, +0.10 on TREND_FOLLOW
- No catalyst + low volume → reduce setup strength by 0.10

**Role 2 — Sentiment Scoring** (continuous):
- Method A (live, free): keyword matching on titles — "upgrade"/"beat" = positive, "downgrade"/"miss" = negative
- Method B (nightly, higher quality): Bedrock batch scoring, stored as `SentimentScore` on SymbolNews
- Feeds into ContextScorer as NewsSentiment [-1, +1]

**Role 3 — Risk Filter** (entry gate):
- Earnings within 1 day → skip entry OR reduce position to 50%
- High news velocity (>5 articles in 2 hours) → reduce size to 50%
- Conflicting news sentiment vs setup direction → reduce setup strength by 0.20

### Context Scorer Formula

```
contextScore = (capitalFlowScore × 0.25 + newsSentiment × 0.30 + shortFloatPressure × 0.15)
               × timeOfDayFactor
               + catalystBoost
               - earningsProximity

capitalFlowScore:    net institutional flow last 3 days, normalized [-1, +1]
newsSentiment:       avg sentiment of recent articles [-1, +1]
shortFloatPressure:  high short + buy setup = squeeze potential [0, +0.3]
catalystBoost:       has catalyst today [0, +0.5]
earningsProximity:   < 3 days to earnings [0, +0.5] penalty
timeOfDayFactor:     avoid first 15 min (0.5), normal hours (1.0), careful last 30 min (0.7)

Clamped to [-1, +1]
```

### L2 Timing Layer (existing, used for entry timing)

When a setup is active, L2 data provides timing confirmation. Same 6 indicators as before:

| Indicator | Default Weight | Range | Source |
|-----------|---------------|-------|--------|
| OBI (smoothed) | 0.25 | [-1, +1] | L2BookCache (30-snapshot average) |
| WOBI (weighted) | 0.25 | [-1, +1] | L2 snapshot (inverse-distance weighted) |
| PressureRoc | 0.15 | [-1, +1] | Short vs long imbalance averages |
| SpreadSignal | 0.10 | [-1, +1] | Spread percentile (tight=bullish) |
| LargeOrderSignal | 0.10 | [-1, +1] | Size spikes > 3x average |
| TickMomentum | 0.15 | [-1, +1] | Uptick/downtick ratio from TickDataCache |

Swin vision model blends when: confidence ≥ 0.40, direction agrees with indicators, indicators have |weighted| ≥ 0.05. Blend cap: 50% max. L2 timing gate: |timingScore| ≥ 0.20 AND direction matches setup.

### Contextual Filters (applied after composite scoring)

Applied in sequence:
1. **Trend filter**: Signal against 15m EMA trend → score × 0.5
2. **VWAP filter**: Signal against VWAP → score × 0.7
3. **Volume boost**: High volume + aligned → score × 1.3
4. **RSI filter**: Graduated — RSI 75-80 → ×0.70, RSI 80-85 → ×0.50, RSI 85+ → ×0.30 (symmetric for oversold)
5. **Floor protection**: 30% — no filter chain can reduce score below 30% of pre-filter value
6. **Hourly multiplier**: Per-hour learned multiplier from model config

### Pre-Market Symbol Scanner

**50 symbols watched permanently** — all receive MQTT data, all have trained models, all have warm caches.

**PreMarketScannerJob** runs at 9:00 AM ET. Ranks all 50, selects top 10 as `IsActiveForTrading` for the day:

| Factor | Weight | Source |
|--------|--------|--------|
| Overnight gap | 0.25 | Pre-market price vs prev close (>2% = high interest) |
| Pre-market volume | 0.20 | Relative to 20-day avg pre-market volume |
| News catalyst | 0.20 | Any catalyst article in last 12 hours |
| Capital flow trend | 0.15 | 3-day net institutional flow direction |
| Technical setup quality | 0.15 | SetupDetector run on yesterday's final bars |
| ATR/Volatility | 0.05 | Minimum ATR% > 0.8% needed for day trading |

Only the active 10 generate trade signals. The other 40 still collect data and train models.

### Real-Time Data Flow

```
Webull MQTT (via injected hook DLL, all 50 symbols subscribed)
  → MqttMessageProcessor (decodes protobuf/JSON)
     → L2 depth: SymbolBookSnapshots DB + L2BookCache + TickDataCache.UpdateL2Features()
     → Quotes: TickDataCache + BarIndicatorService refresh (30s) + TickSnapshots DB (10s)
     → Ticks: TickDataCache (uptick/downtick counts, momentum)

Every 5-min bar close (active 10 only):
  → BarIndicatorService computes 1m/5m/15m indicators → BarIndicatorCache
  → SetupDetector.Scan() → BarSetups DB
  → If setup active + L2 timing confirms:
     → SignalOrchestrator → CompositeScorer → TradingSignals DB
     → PaperTradingExecutor.OnSignalAsync() → PaperTrades DB + Webull paper API
  → PositionMonitor (every 15s) → exit checks → ExitPositionAsync()
```

## Entry Strategy (PaperTradingExecutor)

### Entry Gating (in order)

1. **Auth**: Broker must be authenticated
2. **Opposing setup exit**: If holding position and strong opposing setup fires (strength ≥ 0.50 + L2 confirms) → exit current first
3. **Position limit**: max 3 concurrent positions (including pending entries)
4. **Daily P&L stops**: stop if day P&L ≤ -$1500 or ≥ +$1500
5. **Rate limit**: 1800s (30 min) between trades per symbol
6. **Loss cooldown**: 3600s (60 min) after losing trade on same symbol
7. **Spread regime filter**: reject if spread ≥ 90th percentile
8. **Momentum confirmation**: 5-min price delta alignment ≥ 0.05% of mid price
9. **Min composite score**: ≥ 0.35
10. **Direction enablement**: model_config EnableBuy/EnableSell per ticker
11. **Market hours gate**: no entries before 9:45 AM or after 3:30 PM ET
12. **News risk filter**: earnings within 1 day → skip or half size; high news velocity → half size

### Position Sizing (ATR-based)

```
maxDollars = $30,000 (base)
atrScaleFactor = 0.0015 / atr14Pct, clamped [0.25, 2.0]
strengthFactor = Clamp(|compositeScore| / 0.40, 0.50, 1.0)
newsReduction = 0.50 if (earningsWithin1Day OR highNewsVelocity), else 1.0
finalMaxDollars = maxDollars × atrScaleFactor × strengthFactor × newsReduction
quantity = (int)(finalMaxDollars / entryPrice)
```

### Limit Order Placement

- Price within setup's EntryZone
- Strong signals (|score| ≥ 0.40): cross 50% of spread
- Moderate signals: cross 10% of spread
- Entry order timeout: 300 seconds (5 min)

## Exit Strategy (PositionMonitor)

Exit checks evaluated every **15 seconds** in priority order. Day trading exits are **thesis-aware** — they know the setup type and exit when the thesis dies.

### Exit Thresholds (day trading parameters)

| Threshold | Formula | AMD $218 (ATR $2) |
|-----------|---------|-------------------|
| Stop loss | `Max(setup.StopLevel distance, 1.5×ATR, entrySpread)` | $3.00 |
| Trailing activation | `Max(entryPrice × 0.004, entrySpread × 2)` | $0.87 (0.40%) |
| Breakeven activation | `trailingActivation × 2.5` | $2.18 |
| Profit target | `Max(setup.TargetLevel distance, entryPrice × 2.0%, effectiveStop × 2.0)` | $6.00 |
| Regime exit threshold | `effectiveStop × 0.35` | $1.05 |

### Exit Priority Chain

**EXIT 0: EOD Mandatory Close** — Day trading = no overnight. Non-negotiable.
- 3:30 PM ET: tighten trailing to 20% giveback
- 3:45 PM ET: exit at market if profitable, limit if losing
- 3:50 PM ET: hard close via market order

**EXIT 1: Stop Loss** — Structural stop based on setup's logical level, floored at 1.5×ATR.
- TREND_FOLLOW: below EMA50 or swing low
- VWAP_BOUNCE: below VWAP by 0.3%
- BREAKOUT: mid-point of consolidation range
- REVERSAL: below divergence swing low
- `effectiveStop = Max(setup stop distance, 1.5×ATR, entrySpread)`

**EXIT 2: Profit Target** — `Max(setup.TargetLevel distance, entryPrice × 2.0%, effectiveStop × 2.0)`. Minimum 2:1 risk/reward.

**EXIT 3: Setup Invalidation** — Thesis-specific condition that kills the trade idea.
- Grace period: holdTime × 0.25 (give initial room)
- If trailing active → tighten to 25% giveback
- If trailing NOT active AND losing > 30% of stop → hard exit
- Per setup type:
  - TREND_FOLLOW: EMA20 crosses below EMA50 on 5m
  - VWAP_BOUNCE: price closes below VWAP on 5m bar
  - BREAKOUT: price closes back inside consolidation range
  - REVERSAL: price makes new low below divergence low

**EXIT 4: Trailing Stop** (phased) — Activates when peak profit > entryPrice × 0.40%.
- Anti-wick filter: 15 seconds persistence required
- Base giveback: `0.35 + setupStrength × 0.20` (higher strength → more room)
  - Strength 0.80 → 51% giveback (let winners run)
  - Strength 0.40 → 43% giveback (tighter)
- Tightening overrides from indicators:
  - VWAP cross against position → tighten to 30% (grace: holdTime × 0.40, max 30 min)
  - EMA trend reversal (5m) → tighten to 30% (grace: holdTime × 0.25, max 20 min)
  - RSI extreme (>80 or <20) → tighten to 25% (grace: holdTime × 0.15, max 10 min)
  - RSI moderate extreme (75-80) → tighten to 40%
  - Setup invalidation → tighten to 25% (grace: holdTime × 0.25)
  - EOD (3:30 PM) → tighten to 20%

**EXIT 5: Regime Exit** — When tighteners fire AND trailing NOT active AND losing > 35% of stop → exit. Catches "thesis wrong, exit before full stop" scenarios.

**EXIT 6: Breakeven Stop** — Once peak profit > trailingActivation × 2.5, protect at breakeven minus 0.20× stop buffer. Wider activation than scalping (2.5× vs 2.0×) to give day trades room.

**EXIT 7: Opposing Setup** — If SetupDetector fires opposing setup with strength ≥ 0.50 AND L2 timing confirms (|timing| ≥ 0.20) → exit. Requires full setup, not just L2 blip.

**EXIT 8: Adaptive Time Gate**
- Default holdTime: 3600s (1 hour). Trained nightly per ticker from [1800, 3600, 5400, 7200, 10800, 14400].
- Past holdTime: check setup health + score strength
  - Setup still valid + profitable → hold (up to 2× holdTime)
  - Setup still valid + losing → tighten trailing to 30%
  - Setup invalidated → exit
- Past 2× holdTime: hard cap exit (max 14400s = 4 hours)
- Score=0 handling: data gap, not weakness. Profitable → hold. Losing → exit as TIME+NOSIGNAL.

**EXIT 9: Exit Order Escalation** — If exit order unfilled after 30s, cancel and resubmit crossing spread by 0.05%.

## Daily Schedule

```
9:00 AM ET   PreMarketScannerJob
             → Rank all 50 watched symbols
             → Set top 10 IsActiveForTrading = true
             → Save to DailyWatchlists table

9:30 AM      Market opens. All 50 symbols receiving MQTT data.
             Caches warm for all 50. Active 10 generate signals.

9:45 AM      Entry gate opens (first 15 min avoided)

3:30 PM      EOD tightening begins (trailing → 20% giveback)

3:45 PM      EOD exits start (profitable → market, losing → limit)

3:50 PM      Hard close all remaining positions

4:00 PM      Market close. Clear IsActiveForTrading for all.

9:00 PM ET   NightlyStrategyOptimizer.OptimizeAsync
             → Verify signal outcomes (1hr/2hr/4hr) + backfill gaps
             → Verify BarSetup outcomes (1hr/2hr/4hr, MaxFavorable, MaxAdverse)
             → Score news sentiment via Bedrock (batch)
             → Bedrock AI rule generation (bar + L2 conditions)

9:30 PM ET   NightlyLocalTrainer.TrainAsync
             → Optimize setup weights (EMA, RSI, volume, VWAP, capital flow)
             → Optimize L2 timing weights (existing 6 indicators)
             → Optimize composite blend (setup vs timing vs context ratios)
             → Optimize hold times from [1800, 3600, 5400, 7200, 10800, 14400]
             → Optimize exit thresholds

10:00 PM ET  NightlyStrategyOptimizer.CleanupOldDataAsync
             → L2 snapshots: 3-day retention
             → TickSnapshots: 30-day retention

After close  ml/l2/retrain.py (Swin model for L2 timing, all 50 symbols)
             ml/daytrading/ pipeline (setup model + backtest, as needed)
```

## Nightly Training Details

### NightlyLocalTrainer (C#, hill-climbing)

**What it optimizes** (expanded for day trading):
- **Setup weights**: Feature importances for SetupDetector (EMA alignment, RSI zone, volume, VWAP, capital flow, news). Indices 0-N of the setup weight vector.
- **L2 timing weights**: Same 6 indicators (OBI, WOBI, PressureRoc, Spread, LargeOrder, TickMomentum). Perturbs ±0.05.
- **Composite blend**: Ratio of setup vs timing vs context (sum to 1.0).
- **Score thresholds**: MinScoreToBuy/Sell from candidates [0.25, 0.30, 0.35, 0.40, 0.45, 0.50].
- **Hold time**: From [1800, 3600, 5400, 7200, 10800, 14400] per ticker.
- **Hourly multipliers**: Win rate by ET hour → ScoreMultiplier (>60% → 1.2×, <50% → 0.7×).

**Training data**:
- Multi-horizon outcome: `0.10×PriceAfter1Hr + 0.35×PriceAfter2Hr + 0.35×PriceAfter4Hr + 0.20×MaxFavorable4Hr`
- Walk-forward: 75% train (oldest) / 25% validation (newest), time-ordered by day
- 3 restarts × 300 iterations each (900 total)
- Overfit guard: falls back to defaults if validation P&L < 15% of training P&L

**Training data filters**:
- Only signals with |Score| ≥ 0.20
- Market hours only (9:45 AM - 3:30 PM ET — narrower than collection hours)
- Excludes backfilled signals
- Excludes cold-cache signals (OBI=0 AND WOBI=0 AND TickMomentum=0)
- Requires at least PriceAfter1Hr

### NightlyStrategyOptimizer (C# + Bedrock)

**Rule generation** (expanded for day trading):
- Rules now include bar-level conditions (EMA crossover, RSI zones, VWAP position, volume surge) in addition to L2 conditions
- HoldSeconds: 600-7200s (10 min to 2 hours)
- MaxDailyTrades: 3 per symbol
- Rule preservation: existing rules with ≥5 matches AND positive P&L survive
- Bedrock receives: bar indicators, L2 features, news summaries, capital flow, fundamentals
- Post-generation backtesting on newest 25% of data

**Outcome verification** (expanded):
- Verifies PriceAfter1Hr, PriceAfter2Hr, PriceAfter4Hr on TradingSignalRecord
- Verifies PriceAfter1Hr/2Hr/4Hr, MaxFavorable, MaxAdverse on BarSetups
- News sentiment batch scoring via Bedrock

### Day Trading ML Pipeline (Python, `ml/daytrading/`)

**`export_bars.py`** — Pull from DB: SymbolBars (1m, 5m), SymbolNews, SymbolCapitalFlows, SymbolFinancialSnapshots, BarSetups, TradingSignalRecord. Configurable date range and symbols.

**`compute_features.py`** — Feature engineering per symbol per 5-min bar:
- Technical: EMA9/20/50 (1m), EMA20/50 (5m), RSI14 (1m, 5m), ATR14 (5m), VWAP, VWAP deviation
- Trend: trend_1m, trend_5m, trend_alignment, EMA slope
- Pattern: dist_to_ema20/vwap (ATR-normalized), range_width, range_breakout, RSI divergence
- News: sentiment (12hr avg), news_count_2hr, has_catalyst, catalyst_type
- Fundamental: capital_flow_net, capital_flow_3d, short_float, days_to_earnings
- Volume: volume_ratio, relative_volume (vs 20-day time-of-day avg)
- Labels: weighted return from 1hr/2hr/4hr + max favorable; UP/FLAT/DOWN at ±0.5% (50 bps)

**`train.py`** — LightGBM gradient-boosted trees (tabular features, not images):
- Model A: Setup Quality Classifier → P(UP), P(FLAT), P(DOWN)
- Model B: Setup Type Classifier → best setup type for the conditions
- Walk-forward split by days: 60% train, 20% validation, 20% test
- Threshold optimization on validation set for Sharpe ratio
- Overfit guard: test Sharpe must be >50% of validation Sharpe
- Output: `day_trade_model.json` (thresholds, feature importances, per-symbol params)

**`backtest.py`** — Full pipeline simulation on historical data:
- Replays day by day: setup detection → scoring → entry gating → position management → exits
- Entry price: open of next 1-min bar + slippage (ATR × 0.05 adverse)
- Exit simulation: all 9 exit types on 1-min bar OHLC
- Stop loss: triggered if bar.Low ≤ stop → exit at stop level
- Trailing: update peak from bar.High, check pullback on bar.Close
- L2 timing approximated from volume_spike × trend_alignment × spread_proxy
- Config-driven: `backtest_config.json` for all tweakable parameters
- Output: `trade_log.csv`, `equity_curve.csv`, `metrics.json`

**`evaluate.py`** — Analyze backtest results:
- Overall: Sharpe, max drawdown, win rate, profit factor, avg hold time
- By setup type: which setups profitable?
- By symbol: which symbols work?
- By hour: when to trade?
- By exit reason: are exits working or killing winners?
- By news/catalyst: how much does news matter?
- Threshold sensitivity: entry threshold vs (win_rate, pnl, trade_count)

**`evaluate_scanner.py`** — Evaluate pre-market scanner accuracy:
- Did top 10 picks outperform bottom 40?
- Scanner hit rate (% of picks with >0.5% tradeable move)
- Ranking weight optimization

**Iteration loop**: train → backtest → evaluate → tweak config → re-backtest → retrain if needed → walk-forward validate → deploy.

### Swin Vision Model (Python, `ml/l2/` — entry timing only)

Swin-Tiny (28M params) fine-tuned on L2 order book heatmaps (224×224 RGB). 300 consecutive L2 snapshots encoded as: R=ask sizes, B=bid sizes, G=midprice+spread. Classifies UP/FLAT/DOWN.

**Role in day trading**: Entry timing confirmation only. Does NOT generate trade ideas. Confirms that L2 pressure aligns with the bar-level setup.

**Training**: 20-day window from DB L2 snapshots. Multi-horizon labels: `0.20×5min + 0.40×15min + 0.40×30min`. Pipeline: `export_data.py → render_heatmap.py → train.py → export_onnx.py`.

**Key config** (`ml/l2/config.py`): STRIDE=100, UP/DOWN_THRESHOLD=±30bps, BATCH_SIZE=64, LR=1e-4, EPOCHS_HEAD=5, EPOCHS_FINETUNE=25, PATIENCE=10, SEED=42.

**Data quality**: Market hours only, gap detection (>5s), window span 60-600s, cross-day guard, per-horizon timing tolerance ±30%.

## Project Structure

```
src/
  TradingPilot.Domain.Shared/
    Symbols/
      BarTimeframe.cs                    ── Daily, Hour1, Minute30, Minute15, Minute5, Minute1
      SecurityType.cs                    ── Stock, Etf, Adr, Reit, Spac
      SymbolStatus.cs                    ── Active, Halted, Delisted, Inactive
    Trading/
      SignalSource.cs                    ── L2Micro, BarSetup, AiRule, Composite
      SetupType.cs                       ── TrendFollow, VwapBounce, Breakout, Reversal

  TradingPilot.Domain/
    Symbols/
      Symbol.cs                          ── IsWatched (permanent 50), IsActiveForTrading (daily top 10)
      SymbolBar.cs
      SymbolBookSnapshot.cs
      SymbolCapitalFlow.cs
      SymbolFinancialSnapshot.cs
      SymbolNews.cs                      ── + SentimentScore, CatalystType
    Trading/
      Caches/
        TickDataCache.cs
        BarIndicatorCache.cs             ── 1m + 5m + 15m indicators
        SignalStore.cs
      Signals/
        TradingSignal.cs                 ── runtime DTO
        TradingSignalRecord.cs           ── DB entity with all indicator columns
        IndicatorSnapshot.cs             ── all indicators at a point in time
      Microstructure/
        MarketMicrostructureAnalyzer.cs  ── L2 timing scorer (ComputeTimingScore)
        SwinPredictor.cs                 ── ONNX inference
        L2BookCache.cs                   ── rolling 720 snapshots per ticker
      Setups/
        SetupDetector.cs                 ── scans bars for 4 setup types
        SetupResult.cs                   ── setup DTO (type, direction, levels, strength, expiry)
        BarSetup.cs                      ── DB entity (AppBarSetups table)
      Scoring/
        CompositeScorer.cs               ── setup × 0.50 + timing × 0.30 + context × 0.20
        ContextScorer.cs                 ── news, capital flow, earnings, short float
        ScoringWeights.cs                ── weight config structure
      Rules/
        StrategyConfig.cs                ── bar + L2 conditions
        StrategyRuleEvaluator.cs         ── rule matching + live performance tracking
      Positions/
        PositionState.cs                 ── + SetupType, StopLevel, TargetLevel, InvalidationCheck
        CompletedTrade.cs                ── + Source, SetupType, scores
        PendingOrder.cs
      Config/
        ModelConfig.cs                   ── setup weights, timing weights, composite blend
        DayTradeConfig.cs                ── all day trading constants
      Scanner/
        PreMarketScanner.cs              ── ranking logic (50 → top 10)
        DailyWatchlist.cs                ── DB entity (AppDailyWatchlists table)

  TradingPilot.Application/
    Trading/
      Execution/
        PaperTradingExecutor.cs          ── entry gating for day trading
        PositionMonitor.cs               ── thesis-aware exits, EOD close, setup invalidation
      Analysis/
        SignalOrchestrator.cs            ── SetupDetector → L2 timing → Composite → signal
        BarIndicatorService.cs           ── compute 1m/5m/15m indicators
      Training/
        NightlyLocalTrainer.cs           ── hill-climbing: setup + timing + composite weights
        NightlyStrategyOptimizer.cs      ── verify outcomes, Bedrock rules, news scoring
      Scanner/
        PreMarketScannerJob.cs           ── 9:00 AM ET Hangfire job
      DashboardAppService.cs
    Webull/
      WebullApiClient.cs                 ── bars, depth, news, capital flow, fundamentals, search
      WebullBrokerClient.cs
      WebullPaperTradingClient.cs
      WebullGrpcClient.cs
      WebullProtobufDecoder.cs
      MqttMessageProcessor.cs
      LoadHistoricalBarsJob.cs
      RefreshFundamentalsJob.cs
      RefreshNewsJob.cs
      StartupRecoveryJob.cs
      WebullHookAppService.cs

  TradingPilot.Blazor/
    WebullHookHostedService.cs           ── auto-inject, auto-subscribe all 50 symbols

  TradingPilot.EntityFrameworkCore/
    TradingPilotDbContext.cs
    TradingPilotDbContextModelCreatingExtensions.cs

ml/
  l2/                                    ── Swin L2 heatmap pipeline (entry timing)
    config.py, export_data.py, render_heatmap.py, train.py, export_onnx.py, retrain.py

  daytrading/                            ── Day trading ML + backtesting
    config.py                            ── horizons, thresholds, feature list
    export_bars.py                       ── pull bars + news + flows from DB
    compute_features.py                  ── feature engineering
    train.py                             ── LightGBM setup quality + type classifiers
    backtest.py                          ── full pipeline simulation
    backtest_config.json                 ── tweakable parameters
    exit_engine.py                       ── all 9 exit types (mirrors C# PositionMonitor)
    entry_engine.py                      ── entry gating (mirrors C# PaperTradingExecutor)
    setup_detector.py                    ── setup detection (mirrors C# SetupDetector)
    context_scorer.py                    ── news + fundamentals scoring
    evaluate.py                          ── metrics, breakdowns, comparisons
    evaluate_scanner.py                  ── pre-market scanner accuracy
    configs/                             ── backtest config variants
    results/                             ── per-run output directories
```

## Database Schema

### Existing Tables (unchanged)

**Symbols** — 50 watched symbols. `IsWatched` = permanent watchlist. **ADD**: `IsActiveForTrading` = daily top 10 flag.

**SymbolBars** — OHLCV bars at multiple timeframes (Daily, Hour1, Minute30, Minute15, Minute5, Minute1). Retained forever.

**SymbolBookSnapshots** — L2 order book (bid/ask arrays, spread, midprice, imbalance). 3-day retention. ~110 MB/symbol/day.

**TickSnapshots** — 10-second snapshots with 17 indicators + 7 L2 features. 30-day retention.

**PaperTrades** — Every paper order executed. Retained forever.

**ModelConfigs** — Key-value store for nightly config JSON backup.

### Modified Tables

**SymbolNews** — ADD:
- `SentimentScore` decimal(6,4) — [-1, +1], null until scored
- `SentimentMethod` text — 'KEYWORD' or 'BEDROCK'
- `CatalystType` text — EARNINGS, ANALYST, REGULATORY, SECTOR, CORPORATE, null
- `ScoredAt` timestamp

**SymbolCapitalFlows** — unchanged (already has super-large/large/medium/small inflow/outflow).

**SymbolFinancialSnapshots** — unchanged (already has PE, EPS, MarketCap, ShortFloat, NextEarningsDate).

**TradingSignalRecord** — ADD:
- `Source` text — L2_MICRO, BAR_SETUP, AI_RULE, COMPOSITE
- `SetupType` text — TREND_FOLLOW, VWAP_BOUNCE, BREAKOUT, REVERSAL, null
- `SetupScore` decimal(8,6) — bar-based setup score
- `TimingScore` decimal(8,6) — L2 timing score
- `ContextScore` decimal(8,6) — fundamental/flow/news score
- `Ema50` decimal(12,4) — 50-period EMA on 5m bars
- `Ema20_5m` decimal(12,4)
- `Rsi14_5m` decimal(8,4)
- `TrendStrength` decimal(8,6) — multi-TF trend alignment [-1, +1]
- `VwapDeviation` decimal(8,6) — % distance from VWAP
- `CapitalFlowScore` decimal(8,6) — net institutional flow [-1, +1]
- `RelativeVolume` decimal(8,4) — volume vs 20-day time-of-day avg
- `NewsSentiment` decimal(6,4) — news sentiment at signal time
- `HasCatalyst` bool
- `CatalystType` text
- `NewsCount2Hr` int — articles in last 2 hours
- `PriceAfter1Hr` decimal(12,4)
- `PriceAfter2Hr` decimal(12,4)
- `PriceAfter4Hr` decimal(12,4)
- `WasCorrect1Hr` bool
- `WasCorrect2Hr` bool
- `WasCorrect4Hr` bool

**CompletedTrade** — ADD:
- `Source` text — L2_MICRO, BAR_SETUP, AI_RULE, COMPOSITE
- `SetupType` text
- `SetupScore` decimal(8,6)
- `TimingScore` decimal(8,6)
- `HoldSeconds` int — planned hold at entry
- `StopDistance` decimal(12,4) — effective stop at entry
- `SetupInvalidated` bool — did the thesis break?

### New Tables

**BarSetups** — Detected setups with outcomes. ~20-50 per day per symbol. Retained forever.

| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| SymbolId | Guid (FK) | |
| TickerId | long | |
| Timestamp | DateTime | when setup detected |
| SetupType | text | TREND_FOLLOW, VWAP_BOUNCE, BREAKOUT, REVERSAL |
| Direction | text | BUY, SELL |
| Strength | decimal(8,6) | quality score [0, 1] |
| EntryZoneLow | decimal(12,4) | ideal entry range low |
| EntryZoneHigh | decimal(12,4) | ideal entry range high |
| StopLevel | decimal(12,4) | structural stop price |
| TargetLevel | decimal(12,4) | projected target price |
| ExpiresAt | DateTime | setup shelf life |
| Price | decimal(12,4) | price at detection |
| Ema9, Ema20, Ema50 | decimal(12,4) | 1m indicators |
| Ema20_5m, Ema50_5m | decimal(12,4) | 5m indicators |
| Rsi14, Rsi14_5m | decimal(8,4) | |
| Vwap | decimal(12,4) | |
| Atr14 | decimal(10,4) | |
| VolumeRatio | decimal(8,4) | |
| TrendDirection | int | |
| CapitalFlowScore | decimal(8,6) | |
| NewsSentiment | decimal(6,4) | |
| HasCatalyst | bool | |
| CatalystType | text | |
| NewsCount2Hr | int | |
| PriceAfter1Hr | decimal(12,4)? | outcome verification |
| PriceAfter2Hr | decimal(12,4)? | |
| PriceAfter4Hr | decimal(12,4)? | |
| MaxFavorable | decimal(12,4)? | best price in direction within 4hr |
| MaxAdverse | decimal(12,4)? | worst price against within 4hr |
| WasCorrect1Hr, 2Hr, 4Hr | bool? | |
| WasTradeable | bool? | did L2 timing confirm during window? |
| VerifiedAt | DateTime? | |

Indexes: `(SymbolId, Timestamp)`, `(SetupType, Direction)`

**DailyWatchlists** — Scanner picks per day. One row per day. Retained forever.

| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| Date | DateOnly | trading day |
| Selections | jsonb | [{symbol, tickerId, rank_score, gap_pct, premarket_vol, catalyst_type, setup_quality, atr_pct}] |
| CreatedAt | DateTime | |

## Important Patterns

### Auth Header Resolution

All Webull API jobs use a `ResolveAuthHeader()` pattern: try in-memory `WebullHookAppService.CapturedAuthHeader` first, fall back to reading `D:\Third-Parties\WebullHook\auth_header.json` from disk.

### Database Access in Singletons

Singleton services use `IServiceScopeFactory` to create scoped DB access:
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
(ts."Timestamp" AT TIME ZONE 'UTC') AT TIME ZONE 'America/New_York'
```
In C#: `TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, eastern)`.

### Hangfire Job Attributes

Background jobs use `[DisableConcurrentExecution(seconds)]` and `[AutomaticRetry(Attempts = N)]`. Jobs needing auth use `ResolveAuthHeader()` and return early if unavailable.

### MQTT Subscription (50 symbols)

`WebullHookHostedService.AutoSubscribeAsync()` subscribes all `IsWatched` symbols to 6 MQTT message types (L2depth=91, quote=92, tick=100, trade=102, order=104, L2depth2=105). Re-subscribes every 2 minutes to keep data flowing. Subscriptions are programmatic via `MqttCommandWriter` named pipe — no manual Webull UI interaction needed.

### Config Files (loaded at runtime)

Both live at `D:\Third-Parties\WebullHook\` and are watched via `FileSystemWatcher` + 1-min polling:
- `model_config.json` — learned weights, thresholds, hold times per ticker
- `strategy_rules.json` — AI-generated conditional rules per ticker
- `day_trade_model.json` — LightGBM feature importances and thresholds
- `swin_trading.onnx` — Swin model for L2 timing

### CompletedTrades Table (DISPLAY ONLY)

**CRITICAL RULE**: `CompletedTrades` is used ONLY by the dashboard UI. NOT for trading decisions. Broker API is the sole source of truth for P&L, positions, and order status.

## External Dependencies

- **PostgreSQL** — `localhost:5432/TradingPilot` (connection string in appsettings.json)
- **Redis** — `localhost` database 10 (Hangfire storage + ABP cache)
- **AWS Bedrock** — `us-west-2`, model `anthropic.claude-sonnet-4-6-20250514-v1:0` (nightly rules + news scoring)
- **Webull Desktop** — MQTT hook DLL injected via `ProcessInjector` for real-time data capture

## Locked Parameters (do NOT change without quantitative backtesting evidence)

### Day Trading Parameters

| Parameter | Value | Rationale |
|-----------|-------|-----------|
| Composite weights | setup 0.50, timing 0.30, context 0.20 | Bar setups primary, L2 for timing, context for filtering |
| Stop ATR multiplier | 1.5× (floor) | Setup's structural stop is primary; ATR is minimum floor |
| Trailing activation | 0.40% of entry price | Reachable in 1-4hr holds (0.15% was for scalping) |
| Breakeven activation | 2.5× trailing | Give day trades room to develop |
| Profit target min | 2.0% of entry OR 2.0× stop | Minimum 2:1 risk/reward for day trades |
| Regime exit threshold | 35% of stop | Early exit when thesis weakening |
| Trailing giveback | 0.35 + strength × 0.20 | Higher quality setups get more room |
| Default hold time | 3600s (1 hour) | Nightly optimized per ticker from [1800..14400] |
| Hold time hard cap | 14400s (4 hours) | Must close before EOD |
| Daily P&L stops | ±$1500 | Wider than scalping ±$500 for bigger swings |
| Rate limit | 1800s (30 min) | Fewer, higher conviction trades |
| Loss cooldown | 3600s (60 min) | Prevent repeating losing setups |
| Entry timeout | 300s (5 min) | Wider fills acceptable for day trades |
| Floor protection | 30% | Same as scalping — prevents over-filtering |
| EOD close | 3:50 PM ET hard, 3:30 PM tightening | No overnight positions |
| Entry hours | 9:45 AM - 3:30 PM ET | Avoid open volatility and close illiquidity |
| Active symbols | Top 10 from 50 | Daily scanner selection |
| Swin blend cap | 50% max | Prevents vision model from dominating |
| Swin min conviction | 0.05 | Require L2 indicator confirmation |

### Scanner Parameters

| Factor | Weight | Threshold |
|--------|--------|-----------|
| Overnight gap | 0.25 | >2% = high interest |
| Pre-market volume | 0.20 | >2× avg = high interest |
| News catalyst | 0.20 | Any catalyst = high interest |
| Capital flow trend | 0.15 | Strong directional 3-day flow |
| Setup quality | 0.15 | Any setup strength >0.50 from prior close |
| ATR volatility | 0.05 | ATR% >0.8% minimum for day trading |

## Data Quality Guards

- **ImbalanceVelocity**: Closest-sample interpolation (finds OBI sample nearest 30s ago, normalizes by actual time delta)
- **SpreadPercentile**: Time-based 5-minute window, queue size 600, returns 0.50 if <10 samples
- **Bar history**: 60 bars loaded for EMA20/RSI14 convergence (1m); 120 bars for EMA50 (5m)
- **VWAP**: Filtered to today's ET trading session only
- **BarIndicatorCache.LastRefreshTime**: Per ticker. Analyzer skips if bar indicators >2 minutes stale
- **Setup expiry**: Setups expire 30-60 minutes after detection (shelf life)
- **Cache warm-up**: New symbols need ~6 min L2BookCache (720 snapshots), ~1 min BarIndicatorCache after bars loaded
- **Market hours**: Only signals during 9:45 AM - 3:30 PM ET

## Backtesting Guide

### Quick Start

```bash
cd ml/daytrading
python export_bars.py --days 60           # pull data from DB
python compute_features.py                 # engineer features
python train.py                            # train LightGBM models
python backtest.py                         # simulate trading
python evaluate.py                         # analyze results
```

### Iteration

```bash
# Tweak parameters and re-backtest (no retrain needed)
python backtest.py --config configs/wider_stops.json
python evaluate.py --compare results/baseline results/wider_stops

# Walk-forward validation
python backtest.py --walk-forward --train-days 40 --test-days 20 --step 5

# Single symbol deep dive
python backtest.py --symbol NVDA --verbose
python evaluate.py --symbol NVDA

# Compare with/without news
python backtest.py --config configs/no_news.json --output results/no_news
python evaluate.py --compare results/no_news results/with_news

# Evaluate scanner accuracy
python evaluate_scanner.py --days 60
```

### What to Look For in Results

- **Overall Sharpe < 0**: Model not finding edges. Rethink features or setup conditions.
- **High STOP LOSS count**: Stops too tight. Widen ATR multiplier or use wider structural stops.
- **High TIME CAP count**: Hold times too short. Extend hold candidates.
- **High EOD CLOSE with profit**: Hold time too short — missing moves.
- **TRAILING STOP with low avg P&L**: Trail too tight. Increase giveback %.
- **One setup type negative Sharpe**: Disable that setup type.
- **Scanner hit rate <50%**: Adjust scanner weights. Check which factors predicted movers.

## Reference

See `SYSTEM_REFERENCE.md` for complete database schema details, all Hangfire jobs with schedules, data retention policies, and write volumes.
