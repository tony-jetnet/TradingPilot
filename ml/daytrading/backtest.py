"""
Step 4: Full day-trading pipeline simulation on historical data.

Replays day by day: setup detection → scoring → entry gating → exit management.
Uses 1-min bars for exit price simulation, 5-min bars for setup detection.

Usage:
  python backtest.py
  python backtest.py --config configs/wider_stops.json
  python backtest.py --symbol NVDA --verbose
"""

import argparse
import json
import os
from dataclasses import dataclass, field
from datetime import datetime, timedelta
from zoneinfo import ZoneInfo

import numpy as np
import pandas as pd

import config

ET = ZoneInfo("America/New_York")


def to_naive_utc(ts) -> datetime:
    """Convert any timestamp to naive UTC datetime for consistent arithmetic."""
    if hasattr(ts, 'tz') and ts.tz is not None:
        return ts.tz_localize(None)
    if hasattr(ts, 'tzinfo') and ts.tzinfo is not None:
        return ts.replace(tzinfo=None)
    return ts


@dataclass
class Position:
    symbol: str
    entry_price: float
    entry_time: datetime
    shares: int
    is_long: bool
    setup_type: str
    setup_strength: float
    stop_level: float
    target_level: float
    hold_seconds: int
    peak_price: float = 0.0
    entry_atr: float = 0.0


@dataclass
class Trade:
    symbol: str
    direction: str
    setup_type: str
    entry_time: datetime
    exit_time: datetime
    entry_price: float
    exit_price: float
    shares: int
    pnl: float
    pnl_pct: float
    exit_reason: str
    hold_seconds: float
    setup_strength: float
    composite_score: float = 0.0
    news_sentiment: float = 0.0
    has_catalyst: bool = False


@dataclass
class DayState:
    capital: float
    positions: dict = field(default_factory=dict)  # symbol → Position
    day_pnl: float = 0.0
    last_trade_time: dict = field(default_factory=dict)  # symbol → datetime
    last_trade_pnl: dict = field(default_factory=dict)   # symbol → float


def load_config(config_path: str) -> dict:
    with open(config_path) as f:
        return json.load(f)


def simulate_entry(row: pd.Series, state: DayState, cfg: dict, atr: float) -> Position | None:
    """Check entry gates and create position if valid."""
    symbol = row["symbol"]
    ts = row["ts"]
    price = row["Close"]

    if pd.isna(price) or price <= 0 or np.isnan(price):
        return None

    # Guard against NaN in ATR
    if pd.isna(atr) or np.isnan(atr) or atr <= 0:
        atr = price * 0.01

    # Position limit
    if len(state.positions) >= cfg["max_concurrent_positions"]:
        return None

    # Already in position
    if symbol in state.positions:
        return None

    # Daily P&L stops
    if state.day_pnl <= cfg["daily_pnl_stop_loss"] or state.day_pnl >= cfg["daily_pnl_stop_profit"]:
        return None

    # Rate limit
    if symbol in state.last_trade_time:
        elapsed = (to_naive_utc(ts) - to_naive_utc(state.last_trade_time[symbol])).total_seconds()
        if elapsed < cfg["rate_limit_seconds"]:
            return None

    # Loss cooldown
    if symbol in state.last_trade_pnl and state.last_trade_pnl[symbol] <= 0:
        if symbol in state.last_trade_time:
            elapsed = (to_naive_utc(ts) - to_naive_utc(state.last_trade_time[symbol])).total_seconds()
            if elapsed < cfg["loss_cooldown_seconds"]:
                return None

    # Market hours
    et = ts.astimezone(ET) if ts.tzinfo else ts.replace(tzinfo=ZoneInfo("UTC")).astimezone(ET)
    if et.hour < cfg["entry_start_hour"] or (et.hour == cfg["entry_start_hour"] and et.minute < cfg["entry_start_minute"]):
        return None
    if et.hour > cfg["entry_end_hour"] or (et.hour == cfg["entry_end_hour"] and et.minute > cfg["entry_end_minute"]):
        return None

    # Check if model predicts direction (simplified: use label as proxy in backtest)
    label = row.get("label", 1)
    weighted_ret = row.get("weighted_return", 0)

    if label == 1:  # FLAT — no trade
        return None

    is_long = label == 2  # UP
    direction = 1 if is_long else -1

    # Composite score approximation (in backtest, use feature-based proxy)
    setup_strength = abs(weighted_ret) * 10  # Scale return to 0-1 range
    setup_strength = min(max(setup_strength, 0.3), 0.9)

    trend_alignment = row.get("trend_alignment", 0)
    context_score = row.get("capital_flow_net", 0) * 0.5 + row.get("news_sentiment", 0) * 0.5

    composite = (setup_strength * cfg["composite_weights"]["setup"] +
                 trend_alignment * direction * cfg["composite_weights"]["timing"] +
                 context_score * direction * cfg["composite_weights"]["context"])

    if abs(composite) < cfg["entry_threshold"]:
        return None

    # Position sizing (NaN guards for all computed values)
    if pd.isna(atr) or atr <= 0:
        atr = price * 0.01
    if pd.isna(composite) or np.isnan(composite):
        return None
    max_dollars = cfg["max_position_dollars"]
    atr_pct = atr / price if price > 0 else 0.01
    atr_scale = min(max(0.0015 / atr_pct, 0.25), 2.0)
    max_dollars *= atr_scale
    shares = max(1, int(max_dollars / price))

    # Entry slippage
    slippage = atr * cfg["slippage"]["entry_atr_fraction"]
    entry_price = price + slippage if is_long else price - slippage

    # Stop and target (simplified: ATR-based)
    exit_cfg = cfg["exit_params"]
    stop_dist = atr * exit_cfg["stop_atr_multiplier"]
    target_dist = max(price * exit_cfg["profit_target_min_pct"],
                      stop_dist * exit_cfg["profit_target_min_rr"])

    stop_level = entry_price - stop_dist if is_long else entry_price + stop_dist
    target_level = entry_price + target_dist if is_long else entry_price - target_dist

    return Position(
        symbol=symbol,
        entry_price=entry_price,
        entry_time=ts,
        shares=shares,
        is_long=is_long,
        setup_type=row.get("catalyst_type", "TREND") or "TREND",
        setup_strength=setup_strength,
        stop_level=stop_level,
        target_level=target_level,
        hold_seconds=exit_cfg["hold_seconds_default"],
        peak_price=entry_price,
        entry_atr=atr,
    )


def check_exits(pos: Position, bar: pd.Series, cfg: dict, current_time: datetime) -> tuple[bool, str, float]:
    """Check all exit conditions against a 1-min bar. Returns (should_exit, reason, exit_price)."""
    exit_cfg = cfg["exit_params"]
    high = bar["High"]
    low = bar["Low"]
    close = bar["Close"]
    ct = to_naive_utc(current_time)
    et_entry = to_naive_utc(pos.entry_time)
    elapsed = (ct - et_entry).total_seconds()

    # Update peak
    if pos.is_long and high > pos.peak_price:
        pos.peak_price = high
    elif not pos.is_long and low < pos.peak_price:
        pos.peak_price = low

    profit = (close - pos.entry_price) if pos.is_long else (pos.entry_price - close)
    peak_profit = (pos.peak_price - pos.entry_price) if pos.is_long else (pos.entry_price - pos.peak_price)
    adverse = -profit

    stop_dist = abs(pos.entry_price - pos.stop_level)
    target_dist = abs(pos.target_level - pos.entry_price)

    # EOD hard close — handle both tz-aware and tz-naive timestamps
    try:
        et = current_time.astimezone(ET) if current_time.tzinfo else current_time.replace(tzinfo=ZoneInfo("UTC")).astimezone(ET)
    except Exception:
        # Pandas Timestamp without tz
        et = pd.Timestamp(current_time).tz_localize("UTC").tz_convert(ET)
    if et.hour > cfg["eod_hard_close_hour"] or (et.hour == cfg["eod_hard_close_hour"] and et.minute >= cfg["eod_hard_close_minute"]):
        slippage = pos.entry_atr * cfg["slippage"]["exit_atr_fraction"]
        exit_price = close - slippage if pos.is_long else close + slippage
        return True, "EOD_CLOSE", exit_price

    # Stop loss
    if pos.is_long and low <= pos.stop_level:
        return True, "STOP_LOSS", pos.stop_level
    if not pos.is_long and high >= pos.stop_level:
        return True, "STOP_LOSS", pos.stop_level

    # Profit target
    if pos.is_long and high >= pos.target_level:
        return True, "PROFIT_TARGET", pos.target_level
    if not pos.is_long and low <= pos.target_level:
        return True, "PROFIT_TARGET", pos.target_level

    # Trailing stop
    trailing_activation = pos.entry_price * exit_cfg["trailing_activation_pct"]
    if peak_profit > trailing_activation:
        giveback = exit_cfg["trailing_giveback_base"] + pos.setup_strength * exit_cfg["trailing_giveback_strength_scale"]
        pullback = peak_profit - profit
        if pullback > peak_profit * giveback:
            slippage = pos.entry_atr * cfg["slippage"]["exit_atr_fraction"]
            exit_price = close - slippage if pos.is_long else close + slippage
            return True, "TRAILING_STOP", exit_price

    # Breakeven stop
    breakeven_activation = trailing_activation * exit_cfg["breakeven_activation_multiple"]
    breakeven_buffer = stop_dist * exit_cfg["breakeven_buffer_fraction"]
    if peak_profit > breakeven_activation and profit < -breakeven_buffer:
        slippage = pos.entry_atr * cfg["slippage"]["exit_atr_fraction"]
        exit_price = close - slippage if pos.is_long else close + slippage
        return True, "BREAKEVEN_STOP", exit_price

    # Regime exit (simplified: check if losing >35% of stop)
    if adverse > stop_dist * exit_cfg["regime_exit_stop_fraction"]:
        # Only if past grace period
        if elapsed > pos.hold_seconds * 0.25:
            slippage = pos.entry_atr * cfg["slippage"]["exit_atr_fraction"]
            exit_price = close - slippage if pos.is_long else close + slippage
            return True, "REGIME_EXIT", exit_price

    # Time cap
    max_hold = min(pos.hold_seconds * 2, exit_cfg["max_hold_seconds"])
    if elapsed >= max_hold:
        slippage = pos.entry_atr * cfg["slippage"]["exit_atr_fraction"]
        exit_price = close - slippage if pos.is_long else close + slippage
        return True, "TIME_CAP", exit_price

    return False, "", 0.0


def run_backtest(features_path: str, bars_1m_path: str, cfg: dict,
                 symbol_filter: str | None = None, verbose: bool = False) -> list[Trade]:
    """Run full backtest on historical data."""
    print("Loading data...")
    features = pd.read_parquet(features_path)
    bars_1m = pd.read_parquet(bars_1m_path)

    features["ts"] = pd.to_datetime(features["ts"])
    bars_1m["ts"] = pd.to_datetime(bars_1m["ts"])

    if symbol_filter:
        features = features[features["symbol"] == symbol_filter]
        bars_1m = bars_1m[bars_1m["symbol"] == symbol_filter]

    # Group by trading day
    features["date"] = features["ts"].dt.date
    bars_1m["date"] = bars_1m["ts"].dt.date

    unique_dates = sorted(features["date"].unique())
    print(f"Backtesting {len(unique_dates)} trading days, {features['symbol'].nunique()} symbols")

    all_trades: list[Trade] = []
    state = DayState(capital=cfg["initial_capital"])

    for day in unique_dates:
        # Reset daily state
        state.day_pnl = 0

        day_features = features[features["date"] == day].sort_values("ts")
        day_bars_1m = bars_1m[bars_1m["date"] == day].sort_values("ts")

        if len(day_features) == 0:
            continue

        # Process 5-min bars for entries
        for _, row in day_features.iterrows():
            symbol = row["symbol"]
            atr = row.get("atr14_5m", row.get("Close", 100) * 0.01) or row.get("Close", 100) * 0.01

            # Try entry
            if symbol not in state.positions:
                pos = simulate_entry(row, state, cfg, atr)
                if pos:
                    state.positions[symbol] = pos
                    if verbose:
                        print(f"  ENTRY {row['ts']} {'BUY' if pos.is_long else 'SELL'} {symbol} @ {pos.entry_price:.2f}")

        # Process 1-min bars for exits
        for _, bar in day_bars_1m.iterrows():
            symbol = bar["symbol"]
            if symbol not in state.positions:
                continue

            pos = state.positions[symbol]
            ts = bar["ts"]
            if ts.tzinfo is None:
                ts = ts.replace(tzinfo=ZoneInfo("UTC"))

            should_exit, reason, exit_price = check_exits(pos, bar, cfg, ts)
            if should_exit:
                pnl = (exit_price - pos.entry_price) * pos.shares if pos.is_long \
                    else (pos.entry_price - exit_price) * pos.shares
                pnl_pct = (exit_price - pos.entry_price) / pos.entry_price if pos.is_long \
                    else (pos.entry_price - exit_price) / pos.entry_price

                trade = Trade(
                    symbol=symbol,
                    direction="LONG" if pos.is_long else "SHORT",
                    setup_type=pos.setup_type,
                    entry_time=pos.entry_time,
                    exit_time=ts,
                    entry_price=pos.entry_price,
                    exit_price=exit_price,
                    shares=pos.shares,
                    pnl=pnl,
                    pnl_pct=pnl_pct,
                    exit_reason=reason,
                    hold_seconds=(to_naive_utc(ts) - to_naive_utc(pos.entry_time)).total_seconds(),
                    setup_strength=pos.setup_strength,
                )
                all_trades.append(trade)
                state.day_pnl += pnl
                state.capital += pnl
                state.last_trade_time[symbol] = ts
                state.last_trade_pnl[symbol] = pnl

                del state.positions[symbol]

                if verbose:
                    print(f"  EXIT  {ts} {symbol} @ {exit_price:.2f} | {reason} | P&L=${pnl:.2f}")

        # Force close any remaining positions at EOD
        for symbol in list(state.positions.keys()):
            pos = state.positions[symbol]
            sym_bars = day_bars_1m[day_bars_1m["symbol"] == symbol]
            if len(sym_bars) > 0:
                last_bar = sym_bars.iloc[-1]
                exit_price = last_bar["Close"]
                pnl = (exit_price - pos.entry_price) * pos.shares if pos.is_long \
                    else (pos.entry_price - exit_price) * pos.shares

                trade = Trade(
                    symbol=symbol, direction="LONG" if pos.is_long else "SHORT",
                    setup_type=pos.setup_type, entry_time=pos.entry_time,
                    exit_time=last_bar["ts"], entry_price=pos.entry_price,
                    exit_price=exit_price, shares=pos.shares, pnl=pnl,
                    pnl_pct=(exit_price - pos.entry_price) / pos.entry_price if pos.is_long else (pos.entry_price - exit_price) / pos.entry_price,
                    exit_reason="EOD_FORCE_CLOSE",
                    hold_seconds=(to_naive_utc(last_bar["ts"]) - to_naive_utc(pos.entry_time)).total_seconds(),
                    setup_strength=pos.setup_strength,
                )
                all_trades.append(trade)
                state.capital += pnl

            del state.positions[symbol]

    print(f"\nBacktest complete: {len(all_trades)} trades, final capital=${state.capital:.2f}")
    return all_trades


def save_results(trades: list[Trade], output_dir: str):
    """Save trade log and equity curve."""
    os.makedirs(output_dir, exist_ok=True)

    if not trades:
        print("No trades to save.")
        return

    # Trade log
    trade_dicts = [t.__dict__ for t in trades]
    df = pd.DataFrame(trade_dicts)
    trade_path = os.path.join(output_dir, "trade_log.csv")
    df.to_csv(trade_path, index=False)
    print(f"Trade log: {trade_path}")

    # Equity curve (daily)
    df["date"] = pd.to_datetime(df["exit_time"]).dt.date
    daily = df.groupby("date").agg(
        day_pnl=("pnl", "sum"),
        num_trades=("pnl", "count"),
        num_wins=("pnl", lambda x: (x > 0).sum()),
    ).reset_index()
    daily["cum_pnl"] = daily["day_pnl"].cumsum()

    equity_path = os.path.join(output_dir, "equity_curve.csv")
    daily.to_csv(equity_path, index=False)
    print(f"Equity curve: {equity_path}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", type=str, default=config.BACKTEST_CONFIG_PATH)
    parser.add_argument("--symbol", type=str, default=None)
    parser.add_argument("--verbose", action="store_true")
    parser.add_argument("--output", type=str, default=os.path.join(config.RESULTS_DIR, "latest"))
    args = parser.parse_args()

    cfg = load_config(args.config)
    features_path = os.path.join(config.DATA_DIR, "features.parquet")
    bars_1m_path = os.path.join(config.DATA_DIR, "bars_1m.parquet")

    if not os.path.exists(features_path):
        print("features.parquet not found. Run compute_features.py first.")
        return
    if not os.path.exists(bars_1m_path):
        print("bars_1m.parquet not found. Run export_bars.py first.")
        return

    trades = run_backtest(features_path, bars_1m_path, cfg, args.symbol, args.verbose)
    save_results(trades, args.output)


if __name__ == "__main__":
    main()
