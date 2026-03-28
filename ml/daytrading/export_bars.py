"""
Step 1: Export bar data + news + capital flows + fundamentals from PostgreSQL.

Usage:
  python export_bars.py --days 60
  python export_bars.py --days 30 --symbol NVDA
"""

import argparse
import os
from datetime import datetime, timedelta

import pandas as pd
import psycopg2

import config


def connect_db():
    return psycopg2.connect(
        host=config.DB_HOST, port=config.DB_PORT,
        dbname=config.DB_NAME, user=config.DB_USER, password=config.DB_PASSWORD,
    )


def export_bars(conn, days: int, symbol: str | None = None):
    """Export 1-min and 5-min bars."""
    cutoff = datetime.utcnow() - timedelta(days=days)
    where_symbol = f"""AND s."Id" = '{symbol}'""" if symbol else ""

    for tf_name, tf_val in [("1m", 2), ("5m", 3), ("15m", 4)]:
        sql = f"""
            SELECT s."Id" AS symbol, s."WebullTickerId" AS ticker_id,
                   b."Timestamp" AS ts, b."Open", b."High", b."Low", b."Close", b."Volume",
                   COALESCE(b."Vwap", 0) AS vwap
            FROM "SymbolBars" b
            JOIN "Symbols" s ON s."Id" = b."SymbolId"
            WHERE b."Timeframe" = {tf_val}
              AND b."Timestamp" > %s
              AND s."IsWatched" = true
              {where_symbol}
            ORDER BY s."Id", b."Timestamp"
        """
        df = pd.read_sql(sql, conn, params=[cutoff])
        if len(df) == 0:
            print(f"  No {tf_name} bars found")
            continue

        path = os.path.join(config.DATA_DIR, f"bars_{tf_name}.parquet")
        df.to_parquet(path, index=False)
        print(f"  bars_{tf_name}: {len(df)} rows, {df['symbol'].nunique()} symbols -> {path}")


def export_news(conn, days: int, symbol: str | None = None):
    """Export news with sentiment scores."""
    cutoff = datetime.utcnow() - timedelta(days=days)
    where_symbol = f"""AND n."SymbolId" = '{symbol}'""" if symbol else ""

    sql = f"""
        SELECT n."SymbolId" AS symbol, n."Title", n."Summary",
               n."PublishedAt" AS published_at, n."SentimentScore" AS sentiment,
               n."CatalystType" AS catalyst_type
        FROM "SymbolNews" n
        JOIN "Symbols" s ON s."Id" = n."SymbolId"
        WHERE n."PublishedAt" > %s AND s."IsWatched" = true
        {where_symbol}
        ORDER BY n."SymbolId", n."PublishedAt"
    """
    df = pd.read_sql(sql, conn, params=[cutoff])
    path = os.path.join(config.DATA_DIR, "news.parquet")
    df.to_parquet(path, index=False)
    print(f"  news: {len(df)} rows -> {path}")


def export_capital_flows(conn, days: int, symbol: str | None = None):
    """Export capital flow data."""
    cutoff = (datetime.utcnow() - timedelta(days=days)).date()
    where_symbol = f"""AND f."SymbolId" = '{symbol}'""" if symbol else ""

    sql = f"""
        SELECT f."SymbolId" AS symbol, f."Date" AS date,
               f."SuperLargeInflow", f."SuperLargeOutflow",
               f."LargeInflow", f."LargeOutflow",
               f."MediumInflow", f."MediumOutflow",
               f."SmallInflow", f."SmallOutflow"
        FROM "SymbolCapitalFlows" f
        JOIN "Symbols" s ON s."Id" = f."SymbolId"
        WHERE f."Date" > %s AND s."IsWatched" = true
        {where_symbol}
        ORDER BY f."SymbolId", f."Date"
    """
    df = pd.read_sql(sql, conn, params=[cutoff])
    path = os.path.join(config.DATA_DIR, "capital_flows.parquet")
    df.to_parquet(path, index=False)
    print(f"  capital_flows: {len(df)} rows -> {path}")


def export_fundamentals(conn, days: int, symbol: str | None = None):
    """Export financial snapshots."""
    cutoff = (datetime.utcnow() - timedelta(days=days)).date()
    where_symbol = f"""AND f."SymbolId" = '{symbol}'""" if symbol else ""

    sql = f"""
        SELECT f."SymbolId" AS symbol, f."Date" AS date,
               f."Pe", f."ForwardPe", f."Eps", f."MarketCap",
               f."AvgVolume", f."Beta", f."ShortFloat",
               f."NextEarningsDate" AS next_earnings
        FROM "SymbolFinancialSnapshots" f
        JOIN "Symbols" s ON s."Id" = f."SymbolId"
        WHERE f."Date" > %s AND s."IsWatched" = true
        {where_symbol}
        ORDER BY f."SymbolId", f."Date" DESC
    """
    df = pd.read_sql(sql, conn, params=[cutoff])
    path = os.path.join(config.DATA_DIR, "fundamentals.parquet")
    df.to_parquet(path, index=False)
    print(f"  fundamentals: {len(df)} rows -> {path}")


def export_setups(conn, days: int, symbol: str | None = None):
    """Export detected bar setups with outcomes."""
    cutoff = datetime.utcnow() - timedelta(days=days)
    where_symbol = f"""AND bs."SymbolId" = '{symbol}'""" if symbol else ""

    sql = f"""
        SELECT bs.*
        FROM "BarSetups" bs
        JOIN "Symbols" s ON s."Id" = bs."SymbolId"
        WHERE bs."Timestamp" > %s AND s."IsWatched" = true
        {where_symbol}
        ORDER BY bs."SymbolId", bs."Timestamp"
    """
    df = pd.read_sql(sql, conn, params=[cutoff])
    path = os.path.join(config.DATA_DIR, "setups.parquet")
    df.to_parquet(path, index=False)
    print(f"  setups: {len(df)} rows -> {path}")


def main():
    parser = argparse.ArgumentParser(description="Export bar data for day trading ML")
    parser.add_argument("--days", type=int, default=60)
    parser.add_argument("--symbol", type=str, default=None)
    args = parser.parse_args()

    os.makedirs(config.DATA_DIR, exist_ok=True)
    print(f"Exporting last {args.days} days of data...")

    conn = connect_db()
    try:
        export_bars(conn, args.days, args.symbol)
        export_news(conn, args.days, args.symbol)
        export_capital_flows(conn, args.days, args.symbol)
        export_fundamentals(conn, args.days, args.symbol)
        export_setups(conn, args.days, args.symbol)
    finally:
        conn.close()

    print(f"\nExport complete -> {config.DATA_DIR}")


if __name__ == "__main__":
    main()
