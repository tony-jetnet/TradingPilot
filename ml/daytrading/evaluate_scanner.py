"""
Evaluate pre-market scanner accuracy.

Checks: did top 10 picks outperform bottom 40?
Computes hit rate, average gap capture, opportunity cost.

Usage:
  python evaluate_scanner.py --days 30
"""

import argparse
import os
from datetime import datetime, timedelta

import numpy as np
import pandas as pd
import psycopg2

import config


def load_watchlists(conn, days: int) -> pd.DataFrame:
    """Load DailyWatchlists from DB."""
    cutoff = (datetime.utcnow() - timedelta(days=days)).date()
    sql = """
        SELECT "Date", "Selections", "CreatedAt"
        FROM "DailyWatchlists"
        WHERE "Date" > %s
        ORDER BY "Date"
    """
    return pd.read_sql(sql, conn, params=[cutoff])


def load_daily_returns(conn, days: int) -> pd.DataFrame:
    """Load daily price changes for all watched symbols."""
    cutoff = datetime.utcnow() - timedelta(days=days)
    sql = """
        SELECT s."Id" AS symbol, b."Timestamp"::date AS date,
               FIRST_VALUE(b."Open") OVER (PARTITION BY s."Id", b."Timestamp"::date ORDER BY b."Timestamp") AS day_open,
               LAST_VALUE(b."Close") OVER (PARTITION BY s."Id", b."Timestamp"::date
                   ORDER BY b."Timestamp" ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING) AS day_close,
               MAX(b."High") OVER (PARTITION BY s."Id", b."Timestamp"::date) AS day_high,
               MIN(b."Low") OVER (PARTITION BY s."Id", b."Timestamp"::date) AS day_low
        FROM "SymbolBars" b
        JOIN "Symbols" s ON s."Id" = b."SymbolId"
        WHERE b."Timeframe" = 3
          AND b."Timestamp" > %s
          AND s."IsWatched" = true
        ORDER BY s."Id", b."Timestamp"
    """
    df = pd.read_sql(sql, conn, params=[cutoff])
    if len(df) == 0:
        return df

    # Deduplicate: one row per symbol per day
    df = df.drop_duplicates(subset=["symbol", "date"], keep="last")
    df["day_return"] = (df["day_close"] - df["day_open"]) / df["day_open"]
    df["day_range"] = (df["day_high"] - df["day_low"]) / df["day_open"]

    return df


def evaluate_scanner(conn, days: int):
    """Main evaluation."""
    watchlists = load_watchlists(conn, days)
    returns = load_daily_returns(conn, days)

    if len(watchlists) == 0:
        print("No DailyWatchlists data found. Run the scanner first.")
        return
    if len(returns) == 0:
        print("No bar data found for returns computation.")
        return

    print(f"\n{'='*60}")
    print(f"  SCANNER EVALUATION ({len(watchlists)} days)")
    print(f"{'='*60}\n")

    import json

    selected_returns = []
    unselected_returns = []
    all_symbols = returns["symbol"].unique()

    for _, row in watchlists.iterrows():
        day = row["Date"]
        selections = json.loads(row["Selections"]) if isinstance(row["Selections"], str) else row["Selections"]
        selected_symbols = {s["symbol"] for s in selections}

        day_returns = returns[returns["date"] == day]
        if len(day_returns) == 0:
            continue

        for _, dr in day_returns.iterrows():
            entry = {
                "date": day,
                "symbol": dr["symbol"],
                "day_return": dr["day_return"],
                "day_range": dr["day_range"],
            }
            if dr["symbol"] in selected_symbols:
                selected_returns.append(entry)
            else:
                unselected_returns.append(entry)

    sel_df = pd.DataFrame(selected_returns) if selected_returns else pd.DataFrame()
    unsel_df = pd.DataFrame(unselected_returns) if unselected_returns else pd.DataFrame()

    if len(sel_df) == 0:
        print("No selected symbol return data found.")
        return

    # Metrics
    sel_avg_range = sel_df["day_range"].mean()
    unsel_avg_range = unsel_df["day_range"].mean() if len(unsel_df) > 0 else 0
    sel_avg_return = abs(sel_df["day_return"]).mean()
    unsel_avg_return = abs(unsel_df["day_return"]).mean() if len(unsel_df) > 0 else 0

    # Hit rate: % of selected days where range > 0.5%
    hit_rate = (sel_df["day_range"] > 0.005).mean()

    print(f"Selected symbols (top 10):")
    print(f"  Avg absolute return: {sel_avg_return:.4f} ({sel_avg_return*100:.2f}%)")
    print(f"  Avg day range: {sel_avg_range:.4f} ({sel_avg_range*100:.2f}%)")
    print(f"  Hit rate (range > 0.5%): {hit_rate:.2f} ({hit_rate*100:.0f}%)")
    print(f"  Sample size: {len(sel_df)} symbol-days")

    if len(unsel_df) > 0:
        print(f"\nUnselected symbols (bottom 40):")
        print(f"  Avg absolute return: {unsel_avg_return:.4f} ({unsel_avg_return*100:.2f}%)")
        print(f"  Avg day range: {unsel_avg_range:.4f} ({unsel_avg_range*100:.2f}%)")
        print(f"  Sample size: {len(unsel_df)} symbol-days")

        print(f"\nScanner edge:")
        if unsel_avg_range > 0:
            print(f"  Range advantage: {sel_avg_range/unsel_avg_range:.2f}x ({(sel_avg_range-unsel_avg_range)*100:.2f}% more)")
        if unsel_avg_return > 0:
            print(f"  Return advantage: {sel_avg_return/unsel_avg_return:.2f}x")

    # Per-symbol breakdown
    print(f"\n── TOP SELECTED SYMBOLS (by avg range) ──")
    if len(sel_df) > 0:
        by_sym = sel_df.groupby("symbol").agg(
            avg_range=("day_range", "mean"),
            avg_abs_return=("day_return", lambda x: abs(x).mean()),
            days=("day_return", "count"),
        ).sort_values("avg_range", ascending=False)
        print(by_sym.head(10).to_string())


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--days", type=int, default=30)
    args = parser.parse_args()

    conn = psycopg2.connect(
        host=config.DB_HOST, port=config.DB_PORT,
        dbname=config.DB_NAME, user=config.DB_USER, password=config.DB_PASSWORD,
    )
    try:
        evaluate_scanner(conn, args.days)
    finally:
        conn.close()


if __name__ == "__main__":
    main()
