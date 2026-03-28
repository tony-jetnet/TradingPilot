"""
Step 5: Analyze backtest results.

Computes: Sharpe, drawdown, win rate, profit factor, breakdowns by setup type,
symbol, hour, exit reason, and catalyst presence.

Usage:
  python evaluate.py
  python evaluate.py --results results/latest
  python evaluate.py --compare results/baseline results/tweaked_v2
  python evaluate.py --symbol NVDA
"""

import argparse
import os
import json

import numpy as np
import pandas as pd

import config


def load_trades(results_dir: str) -> pd.DataFrame:
    path = os.path.join(results_dir, "trade_log.csv")
    if not os.path.exists(path):
        print(f"No trade log at {path}")
        return pd.DataFrame()
    df = pd.read_csv(path, parse_dates=["entry_time", "exit_time"])
    return df


def compute_metrics(df: pd.DataFrame) -> dict:
    """Compute overall performance metrics."""
    if len(df) == 0:
        return {"total_trades": 0}

    total_pnl = df["pnl"].sum()
    wins = df[df["pnl"] > 0]
    losses = df[df["pnl"] <= 0]
    win_rate = len(wins) / len(df) if len(df) > 0 else 0

    avg_win = wins["pnl"].mean() if len(wins) > 0 else 0
    avg_loss = abs(losses["pnl"].mean()) if len(losses) > 0 else 0
    profit_factor = (wins["pnl"].sum() / abs(losses["pnl"].sum())) if len(losses) > 0 and losses["pnl"].sum() != 0 else float("inf")

    # Daily Sharpe
    df["date"] = df["exit_time"].dt.date
    daily_pnl = df.groupby("date")["pnl"].sum()
    sharpe = (daily_pnl.mean() / daily_pnl.std() * np.sqrt(252)) if daily_pnl.std() > 0 else 0

    # Max drawdown
    cum_pnl = daily_pnl.cumsum()
    peak = cum_pnl.cummax()
    drawdown = cum_pnl - peak
    max_drawdown = drawdown.min()

    # Avg hold time
    avg_hold = df["hold_seconds"].mean() / 60  # minutes

    # Max consecutive losses
    is_loss = (df.sort_values("exit_time")["pnl"] <= 0).astype(int)
    max_consec_losses = 0
    current = 0
    for v in is_loss:
        current = current + 1 if v else 0
        max_consec_losses = max(max_consec_losses, current)

    return {
        "total_trades": len(df),
        "total_pnl": round(float(total_pnl), 2),
        "win_rate": round(float(win_rate), 4),
        "avg_win": round(float(avg_win), 2),
        "avg_loss": round(float(avg_loss), 2),
        "profit_factor": round(float(profit_factor), 2) if profit_factor != float("inf") else "inf",
        "sharpe": round(float(sharpe), 2),
        "max_drawdown": round(float(max_drawdown), 2),
        "avg_hold_minutes": round(float(avg_hold), 1),
        "max_consecutive_losses": max_consec_losses,
        "avg_trades_per_day": round(len(df) / max(daily_pnl.count(), 1), 1),
    }


def breakdown_by(df: pd.DataFrame, column: str) -> pd.DataFrame:
    """Compute metrics grouped by a column."""
    results = []
    for val, group in df.groupby(column):
        m = compute_metrics(group)
        m[column] = val
        results.append(m)
    return pd.DataFrame(results).sort_values("total_pnl", ascending=False)


def evaluate(results_dir: str, symbol_filter: str | None = None):
    """Run full evaluation."""
    df = load_trades(results_dir)
    if len(df) == 0:
        return

    if symbol_filter:
        df = df[df["symbol"] == symbol_filter]

    print(f"\n{'='*60}")
    print(f"  BACKTEST EVALUATION: {results_dir}")
    print(f"{'='*60}\n")

    # Overall
    metrics = compute_metrics(df)
    print("-- OVERALL --")
    for k, v in metrics.items():
        print(f"  {k}: {v}")

    # By setup type
    if "setup_type" in df.columns:
        print(f"\n-- BY SETUP TYPE --")
        by_setup = breakdown_by(df, "setup_type")
        print(by_setup[["setup_type", "total_trades", "total_pnl", "win_rate", "sharpe"]].to_string(index=False))

    # By symbol
    print(f"\n-- BY SYMBOL --")
    by_symbol = breakdown_by(df, "symbol")
    print(by_symbol[["symbol", "total_trades", "total_pnl", "win_rate", "sharpe"]].to_string(index=False))

    # By exit reason
    if "exit_reason" in df.columns:
        print(f"\n-- BY EXIT REASON --")
        by_exit = breakdown_by(df, "exit_reason")
        print(by_exit[["exit_reason", "total_trades", "total_pnl", "win_rate", "avg_hold_minutes"]].to_string(index=False))

    # By hour
    df["entry_hour"] = df["entry_time"].dt.hour
    print(f"\n-- BY ENTRY HOUR (ET) --")
    by_hour = breakdown_by(df, "entry_hour")
    print(by_hour[["entry_hour", "total_trades", "total_pnl", "win_rate"]].to_string(index=False))

    # By direction
    if "direction" in df.columns:
        print(f"\n-- BY DIRECTION --")
        by_dir = breakdown_by(df, "direction")
        print(by_dir[["direction", "total_trades", "total_pnl", "win_rate", "sharpe"]].to_string(index=False))

    # Save metrics
    metrics_path = os.path.join(results_dir, "metrics.json")
    with open(metrics_path, "w") as f:
        json.dump(metrics, f, indent=2)
    print(f"\nMetrics saved: {metrics_path}")


def compare(dir1: str, dir2: str):
    """Compare two backtest results."""
    df1 = load_trades(dir1)
    df2 = load_trades(dir2)

    if len(df1) == 0 or len(df2) == 0:
        return

    m1 = compute_metrics(df1)
    m2 = compute_metrics(df2)

    print(f"\n{'='*60}")
    print(f"  COMPARISON: {os.path.basename(dir1)} vs {os.path.basename(dir2)}")
    print(f"{'='*60}\n")

    print(f"{'Metric':<25} {'Baseline':>12} {'Tweaked':>12} {'Delta':>12}")
    print("-" * 65)
    for key in m1:
        v1 = m1[key]
        v2 = m2.get(key, "N/A")
        if isinstance(v1, (int, float)) and isinstance(v2, (int, float)):
            delta = v2 - v1
            sign = "+" if delta > 0 else ""
            print(f"  {key:<23} {v1:>12} {v2:>12} {sign}{delta:>11.2f}")
        else:
            print(f"  {key:<23} {v1!s:>12} {v2!s:>12}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--results", type=str, default=os.path.join(config.RESULTS_DIR, "latest"))
    parser.add_argument("--compare", nargs=2, metavar=("DIR1", "DIR2"))
    parser.add_argument("--symbol", type=str, default=None)
    args = parser.parse_args()

    if args.compare:
        compare(args.compare[0], args.compare[1])
    else:
        evaluate(args.results, args.symbol)


if __name__ == "__main__":
    main()
