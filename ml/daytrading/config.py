"""Shared configuration for the day trading ML pipeline."""

import os

# ── Database ──────────────────────────────────────────────────────────
DB_HOST = os.environ.get("DB_HOST", "localhost")
DB_PORT = int(os.environ.get("DB_PORT", "5432"))
DB_NAME = os.environ.get("DB_NAME", "TradingPilot")
DB_USER = os.environ.get("DB_USER", "jetnet")
DB_PASSWORD = os.environ.get("DB_PASSWORD", "JetNet@Core0")

# ── Horizons (day trading: 30 min to 4 hours) ────────────────────────
HORIZON_SECONDS_LIST = [1800, 3600, 7200, 14400]  # 30min, 1hr, 2hr, 4hr
HORIZON_WEIGHTS = [0.10, 0.35, 0.35, 0.20]         # weighted blend for labels

# ── Labels ────────────────────────────────────────────────────────────
UP_THRESHOLD = 0.005     # 50 bps = 0.5% minimum move for UP
DOWN_THRESHOLD = -0.005  # symmetric for DOWN
NUM_CLASSES = 3          # DOWN=0, FLAT=1, UP=2

# ── Feature Engineering ──────────────────────────────────────────────
EMA_PERIODS_1M = [9, 20]
EMA_PERIODS_5M = [20, 50]
RSI_PERIOD = 14
ATR_PERIOD = 14
VWAP_RESET_DAILY = True

# ── Training ─────────────────────────────────────────────────────────
TRAIN_SPLIT = 0.60    # Walk-forward by days: 60% train
VAL_SPLIT = 0.20      #                       20% validation
TEST_SPLIT = 0.20     #                       20% test
SEED = 42
LIGHTGBM_PARAMS = {
    "objective": "multiclass",
    "num_class": 3,
    "metric": "multi_logloss",
    "learning_rate": 0.05,
    "num_leaves": 63,
    "max_depth": 8,
    "min_child_samples": 20,
    "subsample": 0.8,
    "colsample_bytree": 0.8,
    "reg_alpha": 0.1,
    "reg_lambda": 0.1,
    "verbose": -1,
    "seed": SEED,
}
NUM_BOOST_ROUND = 500
EARLY_STOPPING_ROUNDS = 50

# ── Backtesting ──────────────────────────────────────────────────────
BACKTEST_CONFIG_PATH = os.path.join(os.path.dirname(__file__), "backtest_config.json")

# ── Paths ────────────────────────────────────────────────────────────
DATA_DIR = os.path.join(os.path.dirname(__file__), "data")
MODEL_DIR = os.path.join(os.path.dirname(__file__), "models")
RESULTS_DIR = os.path.join(os.path.dirname(__file__), "results")
MODEL_OUTPUT_PATH = os.path.join(os.path.dirname(__file__), "..", "..", "D:", "Third-Parties", "WebullHook", "day_trade_model.json")
# Use a simpler relative path for the model that goes to WebullHook
DEPLOY_MODEL_PATH = r"D:\Third-Parties\WebullHook\day_trade_model.json"
