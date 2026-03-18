"""
Nightly retrain script -- designed to run after market close.

This is the equivalent of NightlyModelTrainer but for the Swin model.
It exports fresh data, renders heatmaps, archives them for accumulation,
then runs Phase 1 (head-only) training on top of the existing best model.

Usage:
  python retrain.py                # full pipeline: export -> render -> archive -> train -> ONNX
  python retrain.py --skip-export  # skip data export (reuse existing CSVs)
  python retrain.py --days 20     # export last 20 days of L2 data
"""

import argparse
import os
import subprocess
import sys
import time
from datetime import datetime

import numpy as np

import config


def run(cmd: list[str], desc: str):
    print(f"\n{'='*60}")
    print(f"  {desc}")
    print(f"{'='*60}\n")
    t0 = time.time()
    result = subprocess.run(cmd, cwd=os.path.dirname(__file__),
                            env={**os.environ, "PYTHONIOENCODING": "utf-8"})
    elapsed = time.time() - t0
    print(f"\n  -> {desc} completed in {elapsed:.0f}s")
    if result.returncode != 0:
        print(f"  FAILED with exit code {result.returncode}")
        sys.exit(result.returncode)


def archive_heatmaps():
    """
    Archive today's rendered heatmaps to a date-stamped file.
    On next retrain, all archived + fresh heatmaps are merged for training.
    This lets training data accumulate beyond the DB retention window.
    """
    archive_dir = os.path.join(config.DATA_DIR, "archive")
    os.makedirs(archive_dir, exist_ok=True)

    images_path = os.path.join(config.DATA_DIR, "images.npy")
    labels_path = os.path.join(config.DATA_DIR, "labels.npy")

    if not os.path.exists(images_path) or not os.path.exists(labels_path):
        print("  No heatmaps to archive")
        return

    today = datetime.utcnow().strftime("%Y%m%d")
    archive_img = os.path.join(archive_dir, f"images_{today}.npy")
    archive_lbl = os.path.join(archive_dir, f"labels_{today}.npy")

    images = np.load(images_path)
    labels = np.load(labels_path)

    # Save today's data (overwrites if retrain runs twice same day)
    np.save(archive_img, images)
    np.save(archive_lbl, labels)
    print(f"  Archived {len(images)} samples to archive/images_{today}.npy")

    # Merge all archived data into the main training files
    all_images = []
    all_labels = []
    archive_files = sorted(f for f in os.listdir(archive_dir) if f.startswith("images_") and f.endswith(".npy"))

    # Keep last 30 days of archives max (~500K samples)
    if len(archive_files) > 30:
        for old_file in archive_files[:-30]:
            old_img = os.path.join(archive_dir, old_file)
            old_lbl = os.path.join(archive_dir, old_file.replace("images_", "labels_"))
            os.remove(old_img)
            if os.path.exists(old_lbl):
                os.remove(old_lbl)
            print(f"  Removed old archive: {old_file}")
        archive_files = archive_files[-30:]

    for af in archive_files:
        img = np.load(os.path.join(archive_dir, af))
        lbl_file = af.replace("images_", "labels_")
        lbl_path = os.path.join(archive_dir, lbl_file)
        if os.path.exists(lbl_path):
            lbl = np.load(lbl_path)
            all_images.append(img)
            all_labels.append(lbl)

    if all_images:
        merged_images = np.concatenate(all_images)
        merged_labels = np.concatenate(all_labels)
        np.save(images_path, merged_images)
        np.save(labels_path, merged_labels)
        print(f"  Merged {len(archive_files)} archives -> {len(merged_images)} total samples")

        # Label distribution
        unique, counts = np.unique(merged_labels, return_counts=True)
        for u, c in zip(unique, counts):
            name = ["DOWN", "FLAT", "UP"][u]
            print(f"    {name}: {c} ({100*c/len(merged_labels):.1f}%)")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--skip-export", action="store_true")
    parser.add_argument("--days", type=int, default=20)
    args = parser.parse_args()

    py = sys.executable

    if not args.skip_export:
        run([py, "export_data.py", "--days", str(args.days)],
            f"Step 1: Export L2 data (last {args.days} days)")

    run([py, "render_heatmap.py", "--preview", "10"],
        "Step 2: Render heatmaps")

    print(f"\n{'='*60}")
    print(f"  Step 2.5: Archive and merge heatmaps")
    print(f"{'='*60}\n")
    archive_heatmaps()

    # Use --head-only for nightly retrain (fast, preserves learned features)
    # Full fine-tune only needed for initial training
    best_model = os.path.join("models", "best.pt")
    if os.path.exists(best_model):
        run([py, "train.py", "--head-only", "--resume", best_model],
            "Step 3: Retrain (head-only, resume from best)")
    else:
        run([py, "train.py"],
            "Step 3: Initial full training (no existing model)")

    run([py, "export_onnx.py"],
        "Step 4: Export ONNX")

    print("\n" + "=" * 60)
    print("  Nightly retrain complete!")
    print(f"  ONNX model: D:\\Third-Parties\\WebullHook\\swin_trading.onnx")
    print(f"  The running Blazor app will auto-reload via FileSystemWatcher")
    print("=" * 60)


if __name__ == "__main__":
    main()
