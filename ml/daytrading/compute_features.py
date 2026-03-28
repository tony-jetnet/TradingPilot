"""
Step 2: Feature engineering from exported bar data.

Computes multi-timeframe technical indicators, news features, capital flow features,
and labels for each 5-min bar. Outputs features.parquet for training and backtesting.

Usage:
  python compute_features.py
  python compute_features.py --symbol NVDA
"""

import argparse
import os
from datetime import datetime, timedelta
from zoneinfo import ZoneInfo

import numpy as np
import pandas as pd

import config

ET = ZoneInfo("America/New_York")


def compute_ema(series: pd.Series, period: int) -> pd.Series:
    return series.ewm(span=period, adjust=False).mean()


def compute_rsi(series: pd.Series, period: int = 14) -> pd.Series:
    delta = series.diff()
    gain = delta.where(delta > 0, 0.0)
    loss = (-delta).where(delta < 0, 0.0)
    avg_gain = gain.ewm(com=period - 1, min_periods=period).mean()
    avg_loss = loss.ewm(com=period - 1, min_periods=period).mean()
    rs = avg_gain / avg_loss.replace(0, np.nan)
    return 100 - 100 / (1 + rs)


def compute_atr(high: pd.Series, low: pd.Series, close: pd.Series, period: int = 14) -> pd.Series:
    prev_close = close.shift(1)
    tr = pd.concat([
        high - low,
        (high - prev_close).abs(),
        (low - prev_close).abs(),
    ], axis=1).max(axis=1)
    return tr.rolling(window=period).mean()


def compute_vwap(df: pd.DataFrame) -> pd.Series:
    """Intraday VWAP that resets daily."""
    tp = (df["High"] + df["Low"] + df["Close"]) / 3
    # Group by date for daily reset
    dates = pd.to_datetime(df["ts"]).dt.date
    cumtpv = (tp * df["Volume"]).groupby(dates).cumsum()
    cumvol = df["Volume"].groupby(dates).cumsum()
    return cumtpv / cumvol.replace(0, np.nan)


def process_symbol(bars_1m: pd.DataFrame, bars_5m: pd.DataFrame, bars_15m: pd.DataFrame,
                    news_df: pd.DataFrame, flows_df: pd.DataFrame, fund_df: pd.DataFrame,
                    symbol: str) -> pd.DataFrame:
    """Compute all features for a single symbol. Returns one row per 5-min bar."""

    if len(bars_5m) < 20:
        return pd.DataFrame()

    # Sort chronologically
    bars_5m = bars_5m.sort_values("ts").copy()
    bars_1m = bars_1m.sort_values("ts").copy() if len(bars_1m) > 0 else bars_1m

    df = bars_5m.copy()
    df["ts"] = pd.to_datetime(df["ts"])

    # ── 5m indicators ──
    df["ema20_5m"] = compute_ema(df["Close"], 20)
    df["ema50_5m"] = compute_ema(df["Close"], 50)
    df["rsi14_5m"] = compute_rsi(df["Close"], 14)
    df["atr14_5m"] = compute_atr(df["High"], df["Low"], df["Close"], 14)
    df["vwap_5m"] = compute_vwap(df)

    # Volume ratio (current vs 20-bar avg)
    df["avg_vol_20"] = df["Volume"].rolling(20).mean()
    df["volume_ratio_5m"] = df["Volume"] / df["avg_vol_20"].replace(0, np.nan)

    # Trend direction
    df["trend_5m"] = np.where(df["ema20_5m"] > df["ema50_5m"], 1,
                     np.where(df["ema20_5m"] < df["ema50_5m"], -1, 0))

    # ── 1m indicators (use the latest 1m bar for each 5m bar) ──
    if len(bars_1m) > 0:
        bars_1m_sorted = bars_1m.sort_values("ts").copy()
        bars_1m_sorted["ema9_1m"] = compute_ema(bars_1m_sorted["Close"], 9)
        bars_1m_sorted["ema20_1m"] = compute_ema(bars_1m_sorted["Close"], 20)
        bars_1m_sorted["rsi14_1m"] = compute_rsi(bars_1m_sorted["Close"], 14)
        bars_1m_sorted["ts"] = pd.to_datetime(bars_1m_sorted["ts"])

        # Align: for each 5m bar, get the last 1m values before that timestamp
        df = df.sort_values("ts")
        bars_1m_sorted = bars_1m_sorted.sort_values("ts")
        df = pd.merge_asof(df, bars_1m_sorted[["ts", "ema9_1m", "ema20_1m", "rsi14_1m"]],
                           on="ts", direction="backward")
        df["trend_1m"] = np.where(df["ema9_1m"] > df["ema20_1m"], 1,
                         np.where(df["ema9_1m"] < df["ema20_1m"], -1, 0))
    else:
        df["ema9_1m"] = df["ema20_5m"]
        df["ema20_1m"] = df["ema50_5m"]
        df["rsi14_1m"] = df["rsi14_5m"]
        df["trend_1m"] = df["trend_5m"]

    # ── 15m indicators ──
    if len(bars_15m) >= 10:
        bars_15m = bars_15m.sort_values("ts").copy()
        bars_15m["ema20_15m"] = compute_ema(bars_15m["Close"], 20)
        bars_15m["ema50_15m"] = compute_ema(bars_15m["Close"], 50)
        bars_15m["rsi14_15m"] = compute_rsi(bars_15m["Close"], 14)
        bars_15m["ts"] = pd.to_datetime(bars_15m["ts"])
        bars_15m["trend_15m"] = np.where(bars_15m["ema20_15m"] > bars_15m["ema50_15m"], 1,
                                np.where(bars_15m["ema20_15m"] < bars_15m["ema50_15m"], -1, 0))
        df = pd.merge_asof(df.sort_values("ts"),
                           bars_15m[["ts", "ema20_15m", "ema50_15m", "rsi14_15m", "trend_15m"]],
                           on="ts", direction="backward")
    else:
        df["ema20_15m"] = df["ema20_5m"]
        df["ema50_15m"] = df["ema50_5m"]
        df["rsi14_15m"] = df["rsi14_5m"]
        df["trend_15m"] = df["trend_5m"]

    # ── Derived features ──
    df["vwap_deviation"] = np.where(df["vwap_5m"] > 0,
                                     (df["Close"] - df["vwap_5m"]) / df["vwap_5m"],
                                     0)
    df["dist_to_ema20_atr"] = np.where(df["atr14_5m"] > 0,
                                        (df["Close"] - df["ema20_5m"]) / df["atr14_5m"],
                                        0)
    df["trend_alignment"] = df["trend_1m"] * 0.3 + df["trend_5m"] * 0.7
    df["ema_slope_5m"] = df["ema20_5m"].diff(3) / df["atr14_5m"].replace(0, np.nan)

    # ── News features (rolling 12h window) ──
    df["news_sentiment"] = 0.0
    df["news_count_2hr"] = 0
    df["has_catalyst"] = False
    df["catalyst_type"] = None

    if len(news_df) > 0:
        news_sym = news_df.copy()
        news_sym["published_at"] = pd.to_datetime(news_sym["published_at"])

        for idx in df.index:
            ts = df.loc[idx, "ts"]
            cutoff_12h = ts - timedelta(hours=12)
            cutoff_2h = ts - timedelta(hours=2)

            recent = news_sym[(news_sym["published_at"] >= cutoff_12h) & (news_sym["published_at"] <= ts)]
            if len(recent) > 0:
                scored = recent[recent["sentiment"].notna()]
                if len(scored) > 0:
                    df.loc[idx, "news_sentiment"] = scored["sentiment"].mean()
                cat = recent[recent["catalyst_type"].notna()].head(1)
                if len(cat) > 0:
                    df.loc[idx, "has_catalyst"] = True
                    df.loc[idx, "catalyst_type"] = cat.iloc[0]["catalyst_type"]
                df.loc[idx, "news_count_2hr"] = len(recent[recent["published_at"] >= cutoff_2h])

    # ── Capital flow features (latest 3 days) ──
    df["capital_flow_net"] = 0.0
    if len(flows_df) > 0:
        flows_sym = flows_df.sort_values("date", ascending=False).head(3)
        total_net = ((flows_sym["SuperLargeInflow"] + flows_sym["LargeInflow"]) -
                     (flows_sym["SuperLargeOutflow"] + flows_sym["LargeOutflow"])).sum()
        total_vol = (flows_sym["SuperLargeInflow"] + flows_sym["LargeInflow"] +
                     flows_sym["SuperLargeOutflow"] + flows_sym["LargeOutflow"]).sum()
        if total_vol > 0:
            df["capital_flow_net"] = np.clip(total_net / total_vol, -1, 1)

    # ── Fundamental features ──
    df["short_float"] = 0.0
    df["days_to_earnings"] = -1
    if len(fund_df) > 0:
        latest_fund = fund_df.sort_values("date", ascending=False).iloc[0]
        df["short_float"] = latest_fund.get("ShortFloat", 0) or 0
        ne = latest_fund.get("next_earnings")
        if ne and isinstance(ne, str) and ne.strip():
            try:
                earn_date = pd.to_datetime(ne).date()
                today = datetime.utcnow().date()
                df["days_to_earnings"] = (earn_date - today).days
            except Exception:
                pass

    # ── Filter to market hours only (9:30-16:00 ET) ──
    df["et_time"] = df["ts"].dt.tz_localize("UTC").dt.tz_convert(ET)
    df["et_hour"] = df["et_time"].dt.hour
    df["et_minute"] = df["et_time"].dt.minute
    df = df[((df["et_hour"] > 9) | ((df["et_hour"] == 9) & (df["et_minute"] >= 30))) &
            (df["et_hour"] < 16)].copy()

    # ── Labels: weighted multi-horizon return ──
    df["symbol"] = symbol
    df = compute_labels(df, bars_1m if len(bars_1m) > 0 else bars_5m)

    # Drop intermediate columns
    cols_to_drop = ["et_time", "avg_vol_20", "Open", "High", "Low", "Volume", "vwap"]
    df.drop(columns=[c for c in cols_to_drop if c in df.columns], inplace=True, errors="ignore")

    return df


def compute_labels(df: pd.DataFrame, price_source: pd.DataFrame) -> pd.DataFrame:
    """Compute multi-horizon weighted return labels."""
    price_source = price_source.copy()
    price_source["ts"] = pd.to_datetime(price_source["ts"])
    price_source = price_source.sort_values("ts").set_index("ts")

    for col in ["price_1hr", "price_2hr", "price_4hr"]:
        df[col] = np.nan

    for idx in df.index:
        ts = df.loc[idx, "ts"]
        entry_price = df.loc[idx, "Close"]
        if entry_price <= 0:
            continue

        for horizon_sec, col in [(3600, "price_1hr"), (7200, "price_2hr"), (14400, "price_4hr")]:
            target_ts = ts + timedelta(seconds=horizon_sec)
            tolerance = timedelta(seconds=horizon_sec * 0.15)

            mask = (price_source.index >= target_ts - tolerance) & (price_source.index <= target_ts + tolerance)
            candidates = price_source.loc[mask]
            if len(candidates) > 0:
                closest_idx = (candidates.index - target_ts).map(lambda x: abs(x.total_seconds())).argmin()
                df.loc[idx, col] = candidates.iloc[closest_idx]["Close"]

    # Weighted return
    df["entry_price"] = df["Close"]
    df["weighted_return"] = 0.0
    df["label"] = 1  # FLAT default

    for idx in df.index:
        ep = df.loc[idx, "entry_price"]
        if ep <= 0:
            continue

        total_weight = 0.0
        weighted_ret = 0.0

        for (col, weight) in [("price_1hr", 0.10), ("price_2hr", 0.35), ("price_4hr", 0.35)]:
            p = df.loc[idx, col]
            if pd.notna(p) and p > 0:
                ret = (p - ep) / ep
                weighted_ret += ret * weight
                total_weight += weight

        # Also include max favorable (approximated from 4hr price range)
        # We use the 4hr return with 0.20 weight as proxy for max favorable
        p4 = df.loc[idx, "price_4hr"]
        if pd.notna(p4) and p4 > 0:
            ret4 = (p4 - ep) / ep
            weighted_ret += abs(ret4) * 0.20 * np.sign(ret4) if ret4 != 0 else 0
            total_weight += 0.20

        if total_weight > 0:
            weighted_ret /= total_weight
            df.loc[idx, "weighted_return"] = weighted_ret

            if weighted_ret > config.UP_THRESHOLD:
                df.loc[idx, "label"] = 2  # UP
            elif weighted_ret < config.DOWN_THRESHOLD:
                df.loc[idx, "label"] = 0  # DOWN

    return df


def main():
    parser = argparse.ArgumentParser(description="Compute features for day trading ML")
    parser.add_argument("--symbol", type=str, default=None)
    args = parser.parse_args()

    print("Loading exported data...")
    bars_1m = pd.read_parquet(os.path.join(config.DATA_DIR, "bars_1m.parquet"))
    bars_5m = pd.read_parquet(os.path.join(config.DATA_DIR, "bars_5m.parquet"))

    bars_15m_path = os.path.join(config.DATA_DIR, "bars_15m.parquet")
    bars_15m = pd.read_parquet(bars_15m_path) if os.path.exists(bars_15m_path) else pd.DataFrame()

    news_path = os.path.join(config.DATA_DIR, "news.parquet")
    news = pd.read_parquet(news_path) if os.path.exists(news_path) else pd.DataFrame()

    flows_path = os.path.join(config.DATA_DIR, "capital_flows.parquet")
    flows = pd.read_parquet(flows_path) if os.path.exists(flows_path) else pd.DataFrame()

    fund_path = os.path.join(config.DATA_DIR, "fundamentals.parquet")
    fund = pd.read_parquet(fund_path) if os.path.exists(fund_path) else pd.DataFrame()

    symbols = bars_5m["symbol"].unique()
    if args.symbol:
        symbols = [s for s in symbols if s == args.symbol]

    print(f"Processing {len(symbols)} symbols...")

    all_features = []
    for sym in symbols:
        print(f"  {sym}...", end=" ")
        sym_1m = bars_1m[bars_1m["symbol"] == sym] if "symbol" in bars_1m.columns else pd.DataFrame()
        sym_5m = bars_5m[bars_5m["symbol"] == sym]
        sym_15m = bars_15m[bars_15m["symbol"] == sym] if len(bars_15m) > 0 else pd.DataFrame()
        sym_news = news[news["symbol"] == sym] if len(news) > 0 else pd.DataFrame()
        sym_flows = flows[flows["symbol"] == sym] if len(flows) > 0 else pd.DataFrame()
        sym_fund = fund[fund["symbol"] == sym] if len(fund) > 0 else pd.DataFrame()

        features = process_symbol(sym_1m, sym_5m, sym_15m, sym_news, sym_flows, sym_fund, sym)
        print(f"{len(features)} rows")
        if len(features) > 0:
            all_features.append(features)

    if not all_features:
        print("No features generated!")
        return

    combined = pd.concat(all_features, ignore_index=True)

    # Label distribution
    labels = combined["label"].value_counts()
    print(f"\nTotal: {len(combined)} rows")
    print(f"  DOWN: {labels.get(0, 0)} ({labels.get(0, 0)/len(combined)*100:.1f}%)")
    print(f"  FLAT: {labels.get(1, 0)} ({labels.get(1, 0)/len(combined)*100:.1f}%)")
    print(f"  UP:   {labels.get(2, 0)} ({labels.get(2, 0)/len(combined)*100:.1f}%)")

    output_path = os.path.join(config.DATA_DIR, "features.parquet")
    combined.to_parquet(output_path, index=False)
    print(f"\nSaved to {output_path}")


if __name__ == "__main__":
    main()
