"""Shared configuration for the L2 heatmap training pipeline."""

# ── Database ──────────────────────────────────────────────────────────
DB_HOST = "localhost"
DB_PORT = 5432
DB_NAME = "TradingPilot"
DB_USER = "jetnet"
DB_PASSWORD = "JetNet@Core0"

# ── Heatmap rendering ────────────────────────────────────────────────
IMAGE_SIZE = 224                # ViT input resolution
WINDOW_SNAPSHOTS = 300          # ~5 min of L2 data per sample
STRIDE_SNAPSHOTS = 30           # slide window by 30 snapshots (~30s) for more samples
PRICE_LEVELS = 200              # Y-axis resolution (number of price rows)
HORIZON_SECONDS = 300           # 5-min forward look for labeling (matches WasCorrect5Min)
UP_THRESHOLD = 0.0002          # min % move to count as UP (2 bps / 2 cents per $100)
DOWN_THRESHOLD = -0.0002       # min % move to count as DOWN

# ── Labels ────────────────────────────────────────────────────────────
# 0 = DOWN, 1 = FLAT, 2 = UP
NUM_CLASSES = 3

# ── Training ──────────────────────────────────────────────────────────
MODEL_NAME = "microsoft/swin-tiny-patch4-window7-224"
BATCH_SIZE = 64
LEARNING_RATE = 1e-4
EPOCHS_HEAD = 5                 # Phase 1: frozen backbone, train head only
EPOCHS_FINETUNE = 15            # Phase 2: unfreeze top layers
WEIGHT_DECAY = 0.01
WARMUP_RATIO = 0.1
TRAIN_SPLIT = 0.8
VAL_SPLIT = 0.1
TEST_SPLIT = 0.1

# ── Paths ─────────────────────────────────────────────────────────────
DATA_DIR = "D:/Third-Parties/TradingPilot/ml/data"
IMAGE_DIR = "D:/Third-Parties/TradingPilot/ml/data/heatmaps"
MODEL_DIR = "D:/Third-Parties/TradingPilot/ml/models"
ONNX_PATH = "D:/Third-Parties/WebullHook/swin_trading.onnx"  # alongside model_config.json
