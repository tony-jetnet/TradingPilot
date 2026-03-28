"""Shared configuration for the L2 heatmap training pipeline."""

import os

# ── Database ──────────────────────────────────────────────────────────
DB_HOST = os.environ.get("DB_HOST", "localhost")
DB_PORT = int(os.environ.get("DB_PORT", "5432"))
DB_NAME = os.environ.get("DB_NAME", "TradingPilot")
DB_USER = os.environ.get("DB_USER", "jetnet")
DB_PASSWORD = os.environ.get("DB_PASSWORD", "JetNet@Core0")

# ── Heatmap rendering ────────────────────────────────────────────────
IMAGE_SIZE = 224                # ViT input resolution
WINDOW_SNAPSHOTS = 300          # ~5 min of L2 data per sample
STRIDE_SNAPSHOTS = 100          # slide window by 100 snapshots (~100s)
                                # Was 30 (90% overlap = highly correlated samples).
                                # 100 gives ~67% overlap — much less sample correlation,
                                # better generalization, and still enough training data.
PRICE_LEVELS = 200              # Y-axis resolution (number of price rows)
HORIZON_SECONDS = 300           # Legacy single-horizon (used for future window sizing)

# Multi-horizon labels: matches NightlyLocalTrainer outcome weighting.
# Prevents Swin from learning short-term mean-reversion patterns that
# conflict with our 20-min hold time (OptimalHoldSeconds=1200).
HORIZON_SECONDS_LIST = [300, 900, 1800]   # 5 min, 15 min, 30 min
HORIZON_WEIGHTS = [0.20, 0.40, 0.40]     # Weighted blend (same as trainer)
MAX_HORIZON_SECONDS = max(HORIZON_SECONDS_LIST)  # 1800s — how far forward to look

UP_THRESHOLD = 0.003            # min % move to count as UP (30 bps)
                                # Was 0.001 (10 bps) — barely above transaction costs.
                                # 30 bps ensures predicted moves are profitable after
                                # spread (~5-10 bps) + slippage (~5 bps).
DOWN_THRESHOLD = -0.003         # min % move to count as DOWN

# ── Labels ────────────────────────────────────────────────────────────
# 0 = DOWN, 1 = FLAT, 2 = UP
NUM_CLASSES = 3

# ── Training ──────────────────────────────────────────────────────────
MODEL_NAME = "microsoft/swin-tiny-patch4-window7-224"
BATCH_SIZE = 64
LEARNING_RATE = 1e-4
EPOCHS_HEAD = 5                 # Phase 1: frozen backbone, train head only
EPOCHS_FINETUNE = 25            # Phase 2: unfreeze top layers (was 15)
WEIGHT_DECAY = 0.01
WARMUP_RATIO = 0.1              # Used for linear warmup in Phase 2
TRAIN_SPLIT = 0.8
VAL_SPLIT = 0.1
TEST_SPLIT = 0.1
EARLY_STOPPING_PATIENCE = 10   # Was 5 — too aggressive for 28M-param model

# ── Reproducibility ──────────────────────────────────────────────────
SEED = 42

# ── Paths ─────────────────────────────────────────────────────────────
DATA_DIR = "D:/Third-Parties/TradingPilot/ml/data"
IMAGE_DIR = "D:/Third-Parties/TradingPilot/ml/data/heatmaps"
MODEL_DIR = "D:/Third-Parties/TradingPilot/ml/models"
ONNX_PATH = "D:/Third-Parties/WebullHook/swin_trading.onnx"  # alongside model_config.json
CHANNEL_STATS_PATH = os.path.join(DATA_DIR, "channel_stats.json")
