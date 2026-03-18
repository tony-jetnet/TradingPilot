"""
Step 2: Render L2 order book snapshots as heatmap images for Swin training.

Each image encodes a time window of ~300 consecutive L2 snapshots:
  - Y-axis: price levels (lowest at bottom, highest at top)
  - X-axis: time (oldest left, newest right)
  - Red channel: ask (sell) order sizes
  - Blue channel: bid (buy) order sizes
  - Green channel: mid-price line + spread region

Usage:
  python render_heatmap.py                       # render all exported CSVs
  python render_heatmap.py --symbol TSLA         # single symbol
  python render_heatmap.py --preview 5           # save 5 preview PNGs for inspection
"""

import argparse
import csv
import os
from dataclasses import dataclass
from datetime import datetime

import numpy as np
from PIL import Image
from tqdm import tqdm

import config


@dataclass
class Snapshot:
    timestamp: datetime
    mid_price: float
    spread: float
    imbalance: float
    depth: int
    bid_prices: list[float]   # 20 levels
    bid_sizes: list[float]
    ask_prices: list[float]
    ask_sizes: list[float]


def load_csv(path: str) -> list[Snapshot]:
    """Load exported CSV into Snapshot list."""
    snapshots = []
    with open(path, "r") as f:
        reader = csv.DictReader(f)
        for row in reader:
            bp = [float(row[f"bid_p{i}"]) for i in range(20)]
            bs = [float(row[f"bid_s{i}"]) for i in range(20)]
            ap = [float(row[f"ask_p{i}"]) for i in range(20)]
            az = [float(row[f"ask_s{i}"]) for i in range(20)]

            snapshots.append(Snapshot(
                timestamp=datetime.fromisoformat(row["timestamp"]),
                mid_price=float(row["mid_price"]),
                spread=float(row["spread"]),
                imbalance=float(row["imbalance"]),
                depth=int(row["depth"]),
                bid_prices=bp,
                bid_sizes=bs,
                ask_prices=ap,
                ask_sizes=az,
            ))
    return snapshots


def render_heatmap(window: list[Snapshot]) -> np.ndarray:
    """
    Render a window of L2 snapshots as a 224×224 RGB image.

    Returns: numpy array of shape (224, 224, 3), dtype uint8
    """
    n_snaps = len(window)
    h = config.IMAGE_SIZE  # 224
    w = config.IMAGE_SIZE

    # 1. Find price range across the entire window
    all_bid_prices = []
    all_ask_prices = []
    for snap in window:
        all_bid_prices.extend([p for p in snap.bid_prices if p > 0])
        all_ask_prices.extend([p for p in snap.ask_prices if p > 0])

    if not all_bid_prices or not all_ask_prices:
        return np.zeros((h, w, 3), dtype=np.uint8)

    price_min = min(all_bid_prices)
    price_max = max(all_ask_prices)
    price_range = price_max - price_min

    if price_range <= 0:
        return np.zeros((h, w, 3), dtype=np.uint8)

    # Add 5% padding on each side
    padding = price_range * 0.05
    price_min -= padding
    price_max += padding
    price_range = price_max - price_min

    # 2. Create the image (R=asks, G=midprice/spread, B=bids)
    img = np.zeros((h, w, 3), dtype=np.float32)

    # Find max size for normalization (use 95th percentile to avoid outlier dominance)
    all_sizes = []
    for snap in window:
        all_sizes.extend(snap.bid_sizes)
        all_sizes.extend(snap.ask_sizes)
    all_sizes = [s for s in all_sizes if s > 0]
    if not all_sizes:
        return np.zeros((h, w, 3), dtype=np.uint8)
    max_size = np.percentile(all_sizes, 95)
    if max_size <= 0:
        max_size = 1.0

    for t_idx, snap in enumerate(window):
        # Map time index to x-pixel column
        x = int(t_idx * (w - 1) / max(n_snaps - 1, 1))

        # Draw bid levels (blue channel)
        for price, size in zip(snap.bid_prices, snap.bid_sizes):
            if price <= 0 or size <= 0:
                continue
            y = _price_to_y(price, price_min, price_range, h)
            if 0 <= y < h:
                intensity = min(size / max_size, 1.0)
                # Use sqrt for better visual dynamic range
                intensity = np.sqrt(intensity)
                img[y, x, 2] = max(img[y, x, 2], intensity)  # Blue

        # Draw ask levels (red channel)
        for price, size in zip(snap.ask_prices, snap.ask_sizes):
            if price <= 0 or size <= 0:
                continue
            y = _price_to_y(price, price_min, price_range, h)
            if 0 <= y < h:
                intensity = min(size / max_size, 1.0)
                intensity = np.sqrt(intensity)
                img[y, x, 0] = max(img[y, x, 0], intensity)  # Red

        # Draw mid-price line (green channel, thin line)
        mid_y = _price_to_y(snap.mid_price, price_min, price_range, h)
        if 0 <= mid_y < h:
            img[mid_y, x, 1] = 1.0  # Green = mid price

    # 3. Convert to uint8
    img = (img * 255).clip(0, 255).astype(np.uint8)

    return img


def _price_to_y(price: float, price_min: float, price_range: float, height: int) -> int:
    """Map price to Y pixel. Higher price = lower Y (top of image)."""
    if price_range <= 0:
        return height // 2
    normalized = (price - price_min) / price_range   # 0 = lowest, 1 = highest
    y = int((1.0 - normalized) * (height - 1))       # flip: high price = top
    return y


def compute_label(window: list[Snapshot], future_snapshots: list[Snapshot]) -> int | None:
    """
    Compute label from price movement after the window.

    Returns: 0=DOWN, 1=FLAT, 2=UP, or None if insufficient future data.
    """
    if not future_snapshots:
        return None

    entry_price = window[-1].mid_price
    if entry_price <= 0:
        return None

    # Use mid-price at the horizon point
    exit_price = future_snapshots[-1].mid_price
    if exit_price <= 0:
        return None

    pct_change = (exit_price - entry_price) / entry_price

    if pct_change > config.UP_THRESHOLD:
        return 2  # UP
    elif pct_change < config.DOWN_THRESHOLD:
        return 0  # DOWN
    else:
        return 1  # FLAT


def generate_samples(
    snapshots: list[Snapshot],
    symbol: str,
    save_previews: int = 0,
) -> tuple[list[np.ndarray], list[int]]:
    """
    Slide a window across snapshots and generate (image, label) pairs.

    Returns: (images, labels) lists
    """
    images = []
    labels = []
    preview_count = 0

    # Estimate how many future snapshots we need for the horizon
    # Snapshots are ~1s apart, horizon is config.HORIZON_SECONDS
    future_needed = config.HORIZON_SECONDS

    total_windows = (len(snapshots) - config.WINDOW_SNAPSHOTS - future_needed) // config.STRIDE_SNAPSHOTS
    if total_windows <= 0:
        print(f"  {symbol}: not enough data for even one window")
        return images, labels

    pbar = tqdm(range(0, len(snapshots) - config.WINDOW_SNAPSHOTS - future_needed, config.STRIDE_SNAPSHOTS),
                desc=f"  {symbol}", unit="win")

    for start in pbar:
        end = start + config.WINDOW_SNAPSHOTS
        window = snapshots[start:end]

        # Future window for labeling
        future = snapshots[end:end + future_needed]
        label = compute_label(window, future)
        if label is None:
            continue

        img = render_heatmap(window)
        images.append(img)
        labels.append(label)

        # Save preview images for visual inspection
        if save_previews > 0 and preview_count < save_previews:
            preview_dir = os.path.join(config.IMAGE_DIR, "previews")
            os.makedirs(preview_dir, exist_ok=True)
            label_name = ["DOWN", "FLAT", "UP"][label]
            preview_path = os.path.join(preview_dir, f"{symbol}_{start}_{label_name}.png")
            Image.fromarray(img).save(preview_path)
            preview_count += 1

    return images, labels


def render_all(symbol_filter: str | None = None, preview: int = 0):
    """Render heatmaps for all exported CSVs and save as numpy arrays."""
    os.makedirs(config.IMAGE_DIR, exist_ok=True)

    # Find all CSV files
    csv_files = sorted(f for f in os.listdir(config.DATA_DIR) if f.startswith("l2_") and f.endswith(".csv"))

    if symbol_filter:
        csv_files = [f for f in csv_files if symbol_filter.upper() in f.upper()]

    if not csv_files:
        print("No CSV files found. Run export_data.py first.")
        return

    all_images = []
    all_labels = []
    label_counts = {0: 0, 1: 0, 2: 0}

    for csv_file in csv_files:
        symbol = csv_file.replace("l2_", "").replace(".csv", "")
        csv_path = os.path.join(config.DATA_DIR, csv_file)

        print(f"Loading {csv_file}...")
        snapshots = load_csv(csv_path)
        print(f"  {len(snapshots)} snapshots loaded")

        images, labels = generate_samples(snapshots, symbol, save_previews=preview)
        all_images.extend(images)
        all_labels.extend(labels)
        for lb in labels:
            label_counts[lb] += 1

    print(f"\nTotal samples: {len(all_images)}")
    print(f"  DOWN: {label_counts[0]}  FLAT: {label_counts[1]}  UP: {label_counts[2]}")

    if all_images:
        # Save as numpy arrays for fast loading during training
        images_arr = np.stack(all_images)  # (N, 224, 224, 3)
        labels_arr = np.array(all_labels, dtype=np.int64)  # (N,)

        np.save(os.path.join(config.DATA_DIR, "images.npy"), images_arr)
        np.save(os.path.join(config.DATA_DIR, "labels.npy"), labels_arr)
        print(f"Saved to {config.DATA_DIR}/images.npy and labels.npy")
        print(f"Images shape: {images_arr.shape}, Labels shape: {labels_arr.shape}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Render L2 heatmaps")
    parser.add_argument("--symbol", type=str, default=None, help="Single symbol filter")
    parser.add_argument("--preview", type=int, default=5, help="Number of preview PNGs to save")
    args = parser.parse_args()

    render_all(symbol_filter=args.symbol, preview=args.preview)
