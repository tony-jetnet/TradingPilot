"""
Step 3: Train LightGBM models for day trading setup classification.

Model A: Setup Quality Classifier → P(UP), P(FLAT), P(DOWN)
Model B: (future) Setup Type Classifier

Walk-forward split by days: 60% train, 20% validation, 20% test.
Threshold optimization on validation set for Sharpe ratio.
Overfit guard: test Sharpe must be >50% of validation Sharpe.

Usage:
  python train.py
  python train.py --symbol NVDA
"""

import argparse
import json
import os
from datetime import datetime

import lightgbm as lgb
import numpy as np
import pandas as pd
from sklearn.metrics import accuracy_score, classification_report

import config

FEATURE_COLS = [
    "Close", "ema20_5m", "ema50_5m", "rsi14_5m", "atr14_5m", "volume_ratio_5m",
    "trend_5m", "ema9_1m", "ema20_1m", "rsi14_1m", "trend_1m",
    "ema20_15m", "ema50_15m", "rsi14_15m", "trend_15m",
    "vwap_deviation", "dist_to_ema20_atr", "trend_alignment", "ema_slope_5m",
    "news_sentiment", "news_count_2hr", "capital_flow_net",
    "short_float", "days_to_earnings", "et_hour", "et_minute",
]


def load_and_split(df: pd.DataFrame):
    """Walk-forward split by unique dates (not random)."""
    df = df.sort_values("ts").copy()
    df["date"] = pd.to_datetime(df["ts"]).dt.date
    unique_dates = sorted(df["date"].unique())

    n_dates = len(unique_dates)
    train_end = int(n_dates * config.TRAIN_SPLIT)
    val_end = int(n_dates * (config.TRAIN_SPLIT + config.VAL_SPLIT))

    train_dates = set(unique_dates[:train_end])
    val_dates = set(unique_dates[train_end:val_end])
    test_dates = set(unique_dates[val_end:])

    train = df[df["date"].isin(train_dates)]
    val = df[df["date"].isin(val_dates)]
    test = df[df["date"].isin(test_dates)]

    return train, val, test


def prepare_features(df: pd.DataFrame):
    """Extract feature matrix and labels."""
    available = [c for c in FEATURE_COLS if c in df.columns]
    X = df[available].copy()

    # Fill NaN with 0 for missing features
    X = X.fillna(0)

    # Convert bool columns to int
    for col in X.columns:
        if X[col].dtype == bool:
            X[col] = X[col].astype(int)

    y = df["label"].values
    return X, y, available


def compute_sharpe(predictions: np.ndarray, true_labels: np.ndarray,
                   returns: np.ndarray, threshold: float = 0.5) -> float:
    """Compute Sharpe-like metric from predictions."""
    # Trade when model is confident: P(UP) > threshold → buy, P(DOWN) > threshold → sell
    p_up = predictions[:, 2] if predictions.ndim > 1 else (predictions == 2).astype(float)
    p_down = predictions[:, 0] if predictions.ndim > 1 else (predictions == 0).astype(float)

    trade_mask = (p_up > threshold) | (p_down > threshold)
    if trade_mask.sum() == 0:
        return 0.0

    direction = np.where(p_up > p_down, 1, -1)
    pnl = direction[trade_mask] * returns[trade_mask]

    if pnl.std() == 0:
        return 0.0

    return float(pnl.mean() / pnl.std() * np.sqrt(252))  # Annualized


def train_model(features_path: str, symbol_filter: str | None = None):
    """Train LightGBM setup quality classifier."""
    print("Loading features...")
    df = pd.read_parquet(features_path)

    if symbol_filter:
        df = df[df["symbol"] == symbol_filter]

    # Drop rows with no label data
    df = df[df["weighted_return"].notna()].copy()

    if len(df) < 100:
        print(f"Not enough data ({len(df)} rows). Need at least 100.")
        return

    print(f"Total samples: {len(df)} ({df['symbol'].nunique()} symbols)")

    # Split
    train, val, test = load_and_split(df)
    print(f"Split: train={len(train)} val={len(val)} test={len(test)}")

    X_train, y_train, feature_names = prepare_features(train)
    X_val, y_val, _ = prepare_features(val)
    X_test, y_test, _ = prepare_features(test)

    # Ensure all splits have same columns
    for col in feature_names:
        if col not in X_val.columns:
            X_val[col] = 0
        if col not in X_test.columns:
            X_test[col] = 0
    X_val = X_val[feature_names]
    X_test = X_test[feature_names]

    print(f"Features: {len(feature_names)}")
    print(f"Label distribution (train): {pd.Series(y_train).value_counts().to_dict()}")

    # Train LightGBM
    dtrain = lgb.Dataset(X_train, label=y_train)
    dval = lgb.Dataset(X_val, label=y_val, reference=dtrain)

    callbacks = [
        lgb.early_stopping(config.EARLY_STOPPING_ROUNDS),
        lgb.log_evaluation(50),
    ]

    model = lgb.train(
        config.LIGHTGBM_PARAMS,
        dtrain,
        num_boost_round=config.NUM_BOOST_ROUND,
        valid_sets=[dval],
        callbacks=callbacks,
    )

    # Evaluate
    val_pred_prob = model.predict(X_val)
    val_pred = val_pred_prob.argmax(axis=1)
    test_pred_prob = model.predict(X_test)
    test_pred = test_pred_prob.argmax(axis=1)

    val_acc = accuracy_score(y_val, val_pred)
    test_acc = accuracy_score(y_test, test_pred)

    print(f"\nValidation accuracy: {val_acc:.3f}")
    print(f"Test accuracy: {test_acc:.3f}")

    print("\nValidation report:")
    print(classification_report(y_val, val_pred, target_names=["DOWN", "FLAT", "UP"], zero_division=0))

    print("Test report:")
    print(classification_report(y_test, test_pred, target_names=["DOWN", "FLAT", "UP"], zero_division=0))

    # Sharpe on validation
    val_returns = val["weighted_return"].values
    test_returns = test["weighted_return"].values

    best_threshold = 0.50
    best_sharpe = -999
    for thresh in [0.40, 0.45, 0.50, 0.55, 0.60, 0.65, 0.70]:
        s = compute_sharpe(val_pred_prob, y_val, val_returns, thresh)
        if s > best_sharpe:
            best_sharpe = s
            best_threshold = thresh

    val_sharpe = best_sharpe
    test_sharpe = compute_sharpe(test_pred_prob, y_test, test_returns, best_threshold)

    print(f"\nBest threshold: {best_threshold:.2f}")
    print(f"Validation Sharpe: {val_sharpe:.2f}")
    print(f"Test Sharpe: {test_sharpe:.2f}")

    # Overfit guard
    if val_sharpe > 0 and test_sharpe < val_sharpe * 0.5:
        print(f"\nWARNING: Possible overfit — test Sharpe ({test_sharpe:.2f}) < 50% of val Sharpe ({val_sharpe:.2f})")

    # Feature importance
    importance = model.feature_importance(importance_type="gain")
    feat_imp = sorted(zip(feature_names, importance), key=lambda x: -x[1])
    print("\nTop 10 features by gain:")
    for name, imp in feat_imp[:10]:
        print(f"  {name}: {imp:.0f}")

    # Save model
    os.makedirs(config.MODEL_DIR, exist_ok=True)
    model_path = os.path.join(config.MODEL_DIR, "setup_quality.txt")
    model.save_model(model_path)
    print(f"\nModel saved: {model_path}")

    # Save model config JSON (consumed by C# SetupDetector and Python backtester)
    model_config = {
        "trained_at": datetime.utcnow().isoformat(),
        "samples": len(df),
        "symbols": list(df["symbol"].unique()),
        "features": feature_names,
        "entry_threshold_buy": best_threshold,
        "entry_threshold_sell": best_threshold,
        "feature_importances": {name: float(imp) for name, imp in feat_imp},
        "validation_metrics": {
            "accuracy": float(val_acc),
            "sharpe": float(val_sharpe),
        },
        "test_metrics": {
            "accuracy": float(test_acc),
            "sharpe": float(test_sharpe),
        },
    }

    config_path = os.path.join(config.MODEL_DIR, "day_trade_model.json")
    with open(config_path, "w") as f:
        json.dump(model_config, f, indent=2)
    print(f"Config saved: {config_path}")

    # Also deploy to WebullHook directory
    try:
        deploy_dir = os.path.dirname(config.DEPLOY_MODEL_PATH)
        if os.path.exists(deploy_dir):
            with open(config.DEPLOY_MODEL_PATH, "w") as f:
                json.dump(model_config, f, indent=2)
            print(f"Deployed to: {config.DEPLOY_MODEL_PATH}")
    except Exception as e:
        print(f"Deploy failed (non-fatal): {e}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--symbol", type=str, default=None)
    args = parser.parse_args()

    features_path = os.path.join(config.DATA_DIR, "features.parquet")
    if not os.path.exists(features_path):
        print(f"Features not found at {features_path}. Run compute_features.py first.")
        return

    train_model(features_path, args.symbol)


if __name__ == "__main__":
    main()
