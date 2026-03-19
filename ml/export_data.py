"""
Step 1: Export L2 order book snapshots from PostgreSQL.

Produces per-symbol CSV files with columns:
  timestamp, mid_price, spread, imbalance, depth,
  bid_p0..bid_p19, bid_s0..bid_s19,
  ask_p0..ask_p19, ask_s0..ask_s19

Usage:
  python export_data.py                    # export all symbols, last 3 days
  python export_data.py --days 7           # last 7 days
  python export_data.py --symbol TSLA      # single symbol
"""

import argparse
import csv
import json
import os
from datetime import datetime, timedelta, timezone

import psycopg2

import config


def connect_db():
    return psycopg2.connect(
        host=config.DB_HOST,
        port=config.DB_PORT,
        dbname=config.DB_NAME,
        user=config.DB_USER,
        password=config.DB_PASSWORD,
    )


def export_snapshots(conn, symbol_id: str | None, days: int):
    os.makedirs(config.DATA_DIR, exist_ok=True)

    cutoff = datetime.now(timezone.utc) - timedelta(days=days)
    cur = conn.cursor()

    # Get distinct symbols
    if symbol_id:
        symbols = [symbol_id]
    else:
        cur.execute(
            """
            SELECT DISTINCT "SymbolId"
            FROM "SymbolBookSnapshots"
            WHERE "Timestamp" >= %s
            ORDER BY "SymbolId"
            """,
            (cutoff,),
        )
        symbols = [row[0] for row in cur.fetchall()]

    print(f"Exporting {len(symbols)} symbols, cutoff={cutoff.isoformat()}")

    for sym in symbols:
        cur.execute(
            """
            SELECT "Timestamp", "MidPrice", "Spread", "Imbalance", "Depth",
                   "BidPrices", "BidSizes", "AskPrices", "AskSizes"
            FROM "SymbolBookSnapshots"
            WHERE "SymbolId" = %s AND "Timestamp" >= %s
            ORDER BY "Timestamp" ASC
            """,
            (sym, cutoff),
        )

        rows = cur.fetchall()
        if not rows:
            print(f"  {sym}: no data, skipping")
            continue

        out_path = os.path.join(config.DATA_DIR, f"l2_{sym}.csv")

        # Build CSV header
        header = ["timestamp", "mid_price", "spread", "imbalance", "depth"]
        for i in range(20):
            header.extend([f"bid_p{i}", f"bid_s{i}"])
        for i in range(20):
            header.extend([f"ask_p{i}", f"ask_s{i}"])

        with open(out_path, "w", newline="") as f:
            writer = csv.writer(f)
            writer.writerow(header)

            valid_count = 0
            skipped_count = 0

            for row in rows:
                ts, mid, spread, imbalance, depth = row[:5]
                bid_prices, bid_sizes, ask_prices, ask_sizes = row[5:]

                # ── Data validation ──────────────────────────────────────
                # Skip rows with obviously bad data to prevent garbage heatmaps
                if mid is None or float(mid) <= 0:
                    skipped_count += 1
                    continue
                if spread is not None and float(spread) < 0:
                    skipped_count += 1
                    continue

                # Parse arrays — could be native arrays or JSON strings
                bp = _parse_array(bid_prices)
                bs = _parse_array(bid_sizes)
                ap = _parse_array(ask_prices)
                az = _parse_array(ask_sizes)

                # Need at least 3 valid bid and ask levels for a useful heatmap
                valid_bids = sum(1 for p in bp if p > 0)
                valid_asks = sum(1 for p in ap if p > 0)
                if valid_bids < 3 or valid_asks < 3:
                    skipped_count += 1
                    continue

                # Pad to 20 levels
                bp = _pad(bp, 20)
                bs = _pad(bs, 20)
                ap = _pad(ap, 20)
                az = _pad(az, 20)

                csv_row = [ts.isoformat(), mid, spread, imbalance, depth]
                for i in range(20):
                    csv_row.extend([bp[i], bs[i]])
                for i in range(20):
                    csv_row.extend([ap[i], az[i]])

                writer.writerow(csv_row)
                valid_count += 1

        print(f"  {sym}: {valid_count} valid / {len(rows)} total ({skipped_count} skipped) -> {out_path}")

    cur.close()


def _parse_array(val) -> list[float]:
    """Parse a PostgreSQL array or JSON array into a list of floats."""
    if val is None:
        return []
    if isinstance(val, (list, tuple)):
        return [float(v) for v in val]
    if isinstance(val, str):
        # Try JSON first, then PostgreSQL array format
        try:
            return [float(v) for v in json.loads(val)]
        except (json.JSONDecodeError, ValueError):
            # PostgreSQL array: {1.0,2.0,3.0}
            cleaned = val.strip("{}").split(",")
            return [float(v) for v in cleaned if v.strip()]
    return []


def _pad(arr: list[float], length: int, fill: float = 0.0) -> list[float]:
    """Pad or truncate array to exact length."""
    if len(arr) >= length:
        return arr[:length]
    return arr + [fill] * (length - len(arr))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Export L2 snapshots from PostgreSQL")
    parser.add_argument("--days", type=int, default=3, help="Days of history to export")
    parser.add_argument("--symbol", type=str, default=None, help="Export single symbol")
    args = parser.parse_args()

    conn = connect_db()
    try:
        export_snapshots(conn, args.symbol, args.days)
    finally:
        conn.close()

    print("Done.")
