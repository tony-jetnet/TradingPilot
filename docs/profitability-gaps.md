# TradingPilot Profitability Gap Analysis

**Date:** 2026-03-18

---

## P0 — Critical (Blocking Profitability)

### 1. No Walk-Forward Validation — Likely Overfitting

The nightly optimizer trains on 20 days of data and evaluates on the *same* data. The hill-climbing optimizer, Bedrock rule generator, and hourly analysis all use in-sample metrics. Zero out-of-sample validation.

What real systems do:
- **Walk-forward analysis**: Train on days 1-15, test on days 16-20, roll forward
- **Train/validation/test split** with no data leakage (the Swin model does this correctly, but weight optimizer and Bedrock rules don't)
- **Paper trade new rules for N days** before going live — a staging period

**Symptom**: Rules and weights look great in backtesting, underperform live. Win rates reported nightly are inflated vs. actual trading results.

### 2. Signal Verification Window Mismatch (1-min/5-min vs Actual Hold)

Signals are verified at exactly 1 and 5 minutes. But hold times range 30-300 seconds (up to 3x = 900s). The mismatch means:
- Optimizing for 1-minute price moves but holding for 1-15 minutes
- A signal profitable at 1 min may reverse by actual exit time
- Training data labels don't match actual trade horizon

**Fix**: Verify at actual hold duration, or use mark-to-market P&L from closed trades as training labels.

---

## P1 — High Impact

### 3. No Proper Backtesting Engine

Trade simulation is unrealistic:
- **No bid-ask spread cost modeling** — uses midprice for PriceAfter, but fills happen at ask (buy) or bid (sell)
- **No slippage modeling** — limit orders don't always fill; backtest assumes 100% fill rate
- **No market impact** — 500 shares into a thin L2 book moves the price
- **Fixed 500 shares** in backtest but variable sizing live
- **No Monte Carlo or bootstrapping** to test robustness

### 4. Position Sizing Is Naive

Current: `Quantity = MaxPositionDollars / Price` (up to 500 shares).

Missing:
- **Kelly Criterion or fractional Kelly** — size based on edge and win rate
- **Volatility normalization** — same dollar allocation for 0.5%/day and 3%/day stocks
- **ATR-based sizing** — `PositionSize = RiskDollars / ATR`
- **Scaling in/out** — partial entries/exits

### 5. No Regime Detection / Market Context Awareness

System trades identically regardless of:
- Trending vs. choppy markets
- High volatility events (FOMC, CPI, earnings)
- Low liquidity periods (lunch hour, pre-market)
- Correlated positions (3 tech stocks = 1 concentrated bet)

Missing: VIX filter, economic calendar, correlation awareness, intraday regime switching.

---

## P2 — Medium Impact

### 6. No Drawdown Controls Beyond Daily Loss Limit

Missing:
- Weekly/monthly drawdown limit
- Max drawdown from peak
- Consecutive loss cooldown
- Volatility-adjusted position sizing
- Portfolio heat tracking

### 7. No Performance Attribution / Strategy Monitoring

Missing:
- Sharpe ratio / Sortino ratio
- Profit factor (needs >1.5 to survive)
- Max consecutive losses tracking
- Per-symbol P&L over time
- Strategy degradation detection
- Expectancy calculation per trade

### 8. No Alerting / Kill Switch

- No notifications for stuck orders, broker disconnect, data gaps
- No automatic kill switch on drawdown threshold
- No alert when a symbol's strategy is consistently losing

---

## P3 — Lower Priority

### 9. Swin Model Structural Issues

- 224x224 heatmap compresses rich numerical data into pixels
- 0.1% UP/DOWN threshold barely above spread on many stocks
- ImageNet pretrained weights may not transfer to financial heatmaps
- No feature importance / interpretability
- Consider tabular model (XGBoost/LightGBM) as supplement

### 10. Commission Model Is Wrong / Missing

- Paper backtest uses $5.98/round-trip, Webull paper is commission-free
- Questrade per-share commissions not modeled
- ECN fees / SEC fees not modeled
- At $500 Questrade positions, commissions = ~1% per side (need 2%+ move to break even)

---

## Priority Implementation Order

| # | Fix | Status |
|---|-----|--------|
| 1 | Walk-forward validation for weight optimizer + Bedrock rules | DONE |
| 2 | Match verification window to actual hold duration | DONE |
| 3 | Realistic backtest engine (spread, fill rate, slippage) | DONE |
| 4 | Volatility-adjusted position sizing (ATR-based) | DONE |
| 5 | Market regime filter (spread-based, blocks 90th pctile) | DONE |
| 6 | Drawdown controls (weekly limit, consecutive loss pause) | TODO |
| 7 | Performance metrics (Sharpe, profit factor, expectancy) | DONE |
| 8 | Alerting system (Telegram/Discord webhook) | TODO |
| 9 | Fix Swin ML pipeline (normalization, thresholds, training) | DONE |
| 10 | Commission-realistic live trading plan | TODO |
