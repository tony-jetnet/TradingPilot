# TradingPilot System Reference

> Database tables, Hangfire jobs, data flows, and retention policies.
> Last updated: 2026-03-16

---

## Database Tables

All tables use prefix `App` (configured in `TradingPilotConsts.DbTablePrefix`), schema `public`.

### 1. Symbols

Metadata for financial instruments being tracked.

| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| Ticker | string(10) | Unique index |
| Name | string(200) | |
| WebullTickerId | long | Unique index |
| WebullExchangeId | int? | |
| Exchange | string(20) | |
| SecurityType | enum | Stock, Etf, Adr, Reit, Spac |
| Sector | string(100) | |
| Industry | string(100) | |
| Status | enum | Active, Halted, Delisted, Inactive |
| ListDate | DateOnly? | |
| IsShortable | bool | Default: true |
| IsMarginable | bool | Default: true |
| IsWatched | bool | Default: false. Filtered index on `true` |

**Written by:** `LoadHistoricalBarsJob` (seeds new symbols, sets IsWatched=true)
**Read by:** `PollL2DepthJob`, `RefreshNewsJob`, `RefreshFundamentalsJob`, `MqttMessageProcessor`, `NightlyStrategyOptimizer` (all query IsWatched=true)
**Retention:** Forever

---

### 2. SymbolBars

OHLCV bars at multiple timeframes (daily, hourly, 30m, 15m, 5m, 1m).

| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| SymbolId | Guid (FK) | |
| Timeframe | enum | Daily, Minute1, Minute5, Minute15, Minute30, Hour1 |
| Timestamp | DateTime | Unique with (SymbolId, Timeframe) |
| Open | decimal(12,4) | |
| High | decimal(12,4) | |
| Low | decimal(12,4) | |
| Close | decimal(12,4) | |
| Volume | long | |
| Vwap | decimal(12,4) | |
| ChangeRatio | decimal(8,6) | |

**Written by:** `LoadHistoricalBarsJob` (startup, per-ticker backfill from Webull API)
**Read by:** `BarIndicatorService` (computes EMA, RSI, VWAP for real-time trading)
**Retention:** Forever

---

### 3. SymbolBookSnapshots

L2 order book depth snapshots (up to 50-level bid/ask). **Largest table by volume.**

| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| SymbolId | Guid (FK) | |
| Timestamp | DateTime | Unique with SymbolId |
| BidPrices | decimal[] (JSONB) | Array of bid price levels |
| BidSizes | decimal[] (JSONB) | Array of bid quantity levels |
| AskPrices | decimal[] (JSONB) | Array of ask price levels |
| AskSizes | decimal[] (JSONB) | Array of ask quantity levels |
| Spread | decimal(10,4) | bestAsk - bestBid |
| MidPrice | decimal(12,4) | (bestBid + bestAsk) / 2 |
| Imbalance | decimal(6,4) | (totalBid - totalAsk) / total |
| Depth | int | Max(bidLevels, askLevels) |

**Written by:** `PollL2DepthJob` (every minute, 12 polls at 5s), `MqttMessageProcessor` (real-time MQTT L2 depth), `StartupRecoveryJob` (immediate snapshot)
**Read by:** `NightlyModelTrainer` (backfills PriceAfter1Min on TradingSignals), `NightlyStrategyOptimizer` (historical analysis)
**Retention:** **3 days** (~110 MB/symbol/day)
**Cleanup:** `NightlyStrategyOptimizer.CleanupOldDataAsync()` at 9:30 PM ET

---

### 4. SymbolNews

News articles from Webull API associated with watched symbols.

| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| SymbolId | Guid (FK) | |
| WebullNewsId | long | Unique with SymbolId |
| Title | string(500) | |
| Summary | string(2000) | |
| SourceName | string(200) | |
| Url | string(1000) | |
| PublishedAt | DateTime | |
| CollectedAt | DateTime | When we fetched it |

**Written by:** `RefreshNewsJob` (every 5 min), `StartupRecoveryJob`
**Read by:** Analysis/display
**Retention:** Forever

---

### 5. SymbolCapitalFlows

Daily institutional money flow data (super-large/large/medium/small inflows and outflows).

| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| SymbolId | Guid (FK) | |
| Date | DateOnly | Unique with SymbolId |
| SuperLargeInflow | decimal(18,4) | |
| SuperLargeOutflow | decimal(18,4) | |
| LargeInflow | decimal(18,4) | |
| LargeOutflow | decimal(18,4) | |
| MediumInflow | decimal(18,4) | |
| MediumOutflow | decimal(18,4) | |
| SmallInflow | decimal(18,4) | |
| SmallOutflow | decimal(18,4) | |
| CollectedAt | DateTime | |

**Written by:** `RefreshFundamentalsJob` (every 30 min, deduped by date)
**Read by:** Fundamental analysis
**Retention:** Forever

---

### 6. SymbolFinancialSnapshots

Daily financial metrics from Webull (P/E, EPS, market cap, 52-week ranges, beta, short float, etc).

| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| SymbolId | Guid (FK) | |
| Date | DateOnly | Unique with SymbolId |
| Pe | decimal(12,4) | Price-to-earnings |
| ForwardPe | decimal(12,4) | Forward P/E |
| Eps | decimal(12,4) | Earnings per share |
| EstEps | decimal(12,4) | Estimated EPS |
| MarketCap | decimal(18,2) | |
| Volume | decimal(18,2) | |
| AvgVolume | decimal(18,2) | |
| High52w | decimal(12,4) | 52-week high |
| Low52w | decimal(12,4) | 52-week low |
| Beta | decimal(8,4) | |
| DividendYield | decimal(8,4) | |
| ShortFloat | decimal(8,4) | Short interest as % of float |
| NextEarningsDate | string(50) | |
| RawJson | JSONB | Full Webull API response |
| CollectedAt | DateTime | |

**Written by:** `RefreshFundamentalsJob` (every 30 min, deduped by date)
**Read by:** Fundamental analysis
**Retention:** Forever

---

### 7. TradingSignals

Buy/sell signals generated by `MarketMicrostructureAnalyzer` from L2 order book analysis. **Critical for nightly training.**

| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| SymbolId | Guid (FK) | |
| TickerId | long | |
| Timestamp | DateTime | |
| Type | enum (byte) | Hold=0, Buy=1, Sell=2 |
| Strength | enum (byte) | Weak=0, Moderate=1, Strong=2 |
| Price | decimal(12,4) | Price at signal time |
| Score | decimal(8,6) | Composite indicator score |
| Reason | string(500) | Human-readable explanation |
| ObiSmoothed | decimal(8,6) | Order book imbalance (30-snapshot smoothed) |
| Wobi | decimal(8,6) | Weighted OBI (distance-weighted) |
| PressureRoc | decimal(8,6) | Book pressure rate of change |
| SpreadSignal | decimal(8,6) | Spread percentile signal |
| LargeOrderSignal | decimal(8,6) | Large order detection signal |
| Spread | decimal(10,4) | Current bid-ask spread |
| Imbalance | decimal(8,6) | Raw OBI |
| BidLevels | int | |
| AskLevels | int | |
| PriceAfter1Min | decimal(12,4)? | Verification: price 1 min later |
| PriceAfter5Min | decimal(12,4)? | Verification: price 5 min later |
| PriceAfter15Min | decimal(12,4)? | Verification: price 15 min later |
| PriceAfter30Min | decimal(12,4)? | Verification: price 30 min later |
| WasCorrect1Min | bool? | Did price move in signal direction? |
| WasCorrect5Min | bool? | |
| WasCorrect15Min | bool? | |
| WasCorrect30Min | bool? | |
| VerifiedAt | DateTime? | When verification was done |

**Written by:** `MqttMessageProcessor` (after signal generation on every L2 depth update)
**Read by:** `NightlyModelTrainer` (20-day lookback, ~1,400+ signals/symbol for weight optimization), `NightlyStrategyOptimizer` (per-hour stats, indicator effectiveness, combinations for Bedrock AI)
**Retention:** Forever (~1.8 MB/symbol/day)

---

### 8. TickSnapshots

Periodic (every ~10 seconds) tick/quote data with all computed indicators. Used by AI optimizer.

| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| SymbolId | Guid (FK) | |
| TickerId | long | |
| Timestamp | DateTime | |
| **Quote data** | | |
| Price | decimal(12,4) | Last trade price |
| Open | decimal(12,4) | |
| High | decimal(12,4) | |
| Low | decimal(12,4) | |
| Volume | long | |
| **Technical indicators** | | |
| Vwap | decimal(12,4) | Volume-weighted average price |
| Ema9 | decimal(12,4) | 9-period exponential moving average |
| Ema20 | decimal(12,4) | 20-period EMA |
| Rsi14 | decimal(8,4) | 14-period relative strength index |
| VolumeRatio | decimal(8,4) | Current volume / average volume |
| **Tick-derived** | | |
| UptickCount | int | Upticks in last 30s |
| DowntickCount | int | Downticks in last 30s |
| TickMomentum | decimal(8,6) | (upticks - downticks) / total, range [-1, +1] |
| **L2-derived features** | | |
| BookDepthRatio | decimal(8,6) | top5 bid+ask size / total size |
| BidWallSize | decimal(10,4) | max(bid size) / avg(bid size) |
| AskWallSize | decimal(10,4) | max(ask size) / avg(ask size) |
| BidSweepCost | decimal(12,2) | Shares to move price down $0.10 |
| AskSweepCost | decimal(12,2) | Shares to move price up $0.10 |
| ImbalanceVelocity | decimal(10,6) | (currentOBI - OBI30sAgo) / 30 |
| SpreadPercentile | decimal(6,4) | Rank of spread in last 5 min (0=tight, 1=wide) |

**Written by:** `MqttMessageProcessor.TryStoreTickSnapshotAsync()` (every 10s per ticker, includes L2-derived features from `TickDataCache`)
**Read by:** `NightlyStrategyOptimizer` (joins with TradingSignals to analyze L2 feature effectiveness and indicator combinations)
**Retention:** **30 days** (~3.7 MB/symbol/day)
**Cleanup:** `NightlyStrategyOptimizer.CleanupOldDataAsync()` at 9:30 PM ET

---

### 9. PaperTrades

Record of every paper trading order executed.

| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| SymbolId | Guid (FK) | |
| TickerId | long | |
| Timestamp | DateTime | |
| Action | string(10) | "BUY" or "SELL" |
| Quantity | int | Shares traded |
| SignalPrice | decimal(12,4) | Price when signal was generated |
| FilledPrice | decimal(12,4)? | Actual fill price from Webull |
| Score | decimal(8,6) | Composite signal score |
| Reason | string(500) | Entry/exit reason (e.g., "EXIT TIME 62s P&L=$3.20net") |
| WebullOrderId | long? | Webull's order ID |
| OrderStatus | string(100) | "Placed", "Failed: ...", etc. |
| SignalId | Guid? | Link to triggering TradingSignal |

**Written by:** `PaperTradingExecutor.PlaceOrderAsync()` (entry: score >= threshold + momentum; exit: time stop / stop loss / opposing signal)
**Read by:** P&L analysis
**Retention:** Forever

---

### 10. ModelConfigs

Key-value store for nightly-trained configuration JSON (backup of file-based configs).

| Column | Type | Notes |
|--------|------|-------|
| Id | Guid (PK) | |
| Key | string | Unique. "nightly_model_config" or "strategy_rules" |
| Value | JSONB | Full JSON config |
| UpdatedAt | DateTime | |

**Written by:** `NightlyModelTrainer` (upserts `nightly_model_config`), `NightlyStrategyOptimizer` (upserts `strategy_rules`)
**Read by:** Recovery/fallback
**Retention:** Forever

---

## Data Retention Summary

| Table | Retention | Daily Size/Symbol | Reason |
|-------|-----------|-------------------|--------|
| SymbolBookSnapshots | **3 days** | ~110 MB | Huge volume; only needed for short-term PriceAfter verification |
| TickSnapshots | **30 days** | ~3.7 MB | Needed for AI training lookback (20 days + buffer) |
| TradingSignals | Forever | ~1.8 MB | Training data for both hill-climbing and AI optimization |
| SymbolBars | Forever | Tiny | Historical OHLCV for indicator computation |
| PaperTrades | Forever | Tiny | Audit trail of all trades |
| Symbols | Forever | Static | Reference data |
| SymbolNews | Forever | Small | Contextual info |
| SymbolCapitalFlows | Forever | Tiny (1 row/day) | Institutional flow analysis |
| SymbolFinancialSnapshots | Forever | Tiny (1 row/day) | Fundamental analysis |
| ModelConfigs | Forever | Tiny (2 rows) | Config backup |

**Cleanup runs:** Nightly at 9:30 PM ET weekdays via `NightlyStrategyOptimizer.CleanupOldDataAsync()`

---

## Hangfire Jobs

All jobs use Redis storage (database 10, prefix `TradingPilot:`).

### Recurring Jobs

| Job ID | Schedule | Class | Purpose |
|--------|----------|-------|---------|
| `poll-l2-depth` | Every minute | `PollL2DepthJob` | Polls L2 order book for watched symbols (12 polls/min at 5s intervals). Stores `SymbolBookSnapshots`. |
| `refresh-news` | Every 5 min | `RefreshNewsJob` | Fetches news for watched symbols from Webull. Stores `SymbolNews`. |
| `refresh-fundamentals` | Every 30 min | `RefreshFundamentalsJob` | Fetches capital flow + financial metrics. Stores `SymbolCapitalFlows` + `SymbolFinancialSnapshots`. |
| `nightly-model-training` | 9:00 PM ET, Mon-Fri | `NightlyModelTrainer` | Hill-climbing weight optimization from 20-day signal history. Outputs `model_config.json`. |
| `nightly-strategy-optimization` | 9:15 PM ET, Mon-Fri | `NightlyStrategyOptimizer` | AI pattern discovery via Bedrock Sonnet 4.6. One API call per symbol. Outputs `strategy_rules.json`. |
| `nightly-data-cleanup` | 9:30 PM ET, Mon-Fri | `NightlyStrategyOptimizer` | Deletes old data per retention policy (SymbolBookSnapshots > 3d, TickSnapshots > 30d). |

### One-Shot Startup Jobs

| Job | Delay | Class | Purpose |
|-----|-------|-------|---------|
| `LoadHistoricalBarsJob` (per ticker) | 30s + (i*60s) staggered | `LoadHistoricalBarsJob` | Backfills OHLCV bars from Webull API (6 timeframes). Seeds `Symbols` if needed. |
| `StartupRecoveryJob` | 30s | `StartupRecoveryJob` | Takes immediate L2 snapshot + backfills news. Lightweight — heavy backfill runs nightly. |

### Nightly Job Sequence

```
9:00 PM ET  NightlyModelTrainer.TrainAsync(20)
            ├── Reads: TradingSignals (20-day lookback, ~1,400+ rows/symbol)
            ├── Reads: SymbolBookSnapshots (backfills PriceAfter1Min)
            ├── Process: Hill-climbing weight optimization per ticker
            ├── Writes: model_config.json (file)
            └── Writes: ModelConfigs table (backup)

9:15 PM ET  NightlyStrategyOptimizer.OptimizeAsync(20)
            ├── Step 1: Backfill TickSnapshots from 1-min bars (fills gaps from app downtime)
            ├── Step 2: Backfill TradingSignals from TickSnapshots (with PriceAfter verification)
            ├── Step 3: Per-symbol DB analysis (hourly stats, indicator effectiveness, combinations)
            ├── Step 4: 1 Bedrock API call per symbol (~10 calls)
            ├── Writes: strategy_rules.json (file) → auto-loaded by MarketMicrostructureAnalyzer
            └── Writes: ModelConfigs table (backup)

9:30 PM ET  NightlyStrategyOptimizer.CleanupOldDataAsync()
            ├── Deletes: SymbolBookSnapshots older than 3 days
            └── Deletes: TickSnapshots older than 30 days
```

---

## Real-Time Data Flow

```
Webull MQTT stream
  │
  ├── L2 Depth (protobuf/JSON)
  │     ├── SymbolBookSnapshots (DB)
  │     ├── L2BookCache (in-memory, 720 snapshots/ticker)
  │     ├── TickDataCache.UpdateL2Features() (computes 7 L2 features)
  │     ├── MarketMicrostructureAnalyzer.AnalyzeSnapshot()
  │     │     ├── Stage 1: StrategyRuleEvaluator (AI rules) → signal if match
  │     │     └── Stage 2: Weighted scoring (model_config) → signal if threshold met
  │     ├── TradingSignals (DB, if signal generated)
  │     └── PaperTradingExecutor.OnSignalAsync()
  │           └── PaperTrades (DB, if trade executed)
  │
  ├── Quote Update (protobuf)
  │     ├── TickDataCache (price, OHLCV)
  │     ├── BarIndicatorService refresh (every 30s)
  │     └── TickSnapshots (DB, every 10s per ticker)
  │
  └── Tick (protobuf)
        └── TickDataCache (uptick/downtick counts, momentum)
```
