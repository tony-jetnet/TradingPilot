"""
Nightly retrain script -- designed to run after market close.

This is the equivalent of NightlyModelTrainer but for the Swin model.
It exports fresh L2 data (20-day window), renders heatmaps, trains,
and exports ONNX.

No archiving needed: the 20-day DB export already covers the optimal
training window. Older data reflects stale market regimes and hurts
more than it helps for fine-tuning.

Usage:
  python retrain.py                # full pipeline: export -> render -> train -> ONNX
  python retrain.py --skip-export  # skip data export (reuse existing CSVs)
  python retrain.py --days 20     # export last 20 days of L2 data
"""

import argparse
import os
import subprocess
import sys
import time

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

    # Full fine-tune (Phase 1 head + Phase 2 top layers) — ImageNet features
    # don't transfer well to L2 heatmaps, so backbone needs adaptation.
    best_model = os.path.join("models", "best.pt")
    if os.path.exists(best_model):
        run([py, "train.py", "--resume", best_model],
            "Step 3: Retrain (full fine-tune, resume from best)")
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
