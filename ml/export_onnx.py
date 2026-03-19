"""
Step 4: Export trained Swin-Tiny model to ONNX for .NET integration.

Produces:
  - swin_trading.onnx  → load in C# via Microsoft.ML.OnnxRuntime.Gpu
  - model_meta.json    → metadata (class names, normalization params, image size)

Usage:
  python export_onnx.py                              # export best model
  python export_onnx.py --checkpoint models/best.pt  # specific checkpoint
"""

import argparse
import json
import os

import numpy as np
import onnx
import torch

import config
from train import build_model


def export_onnx(checkpoint_path: str, output_path: str):
    device = torch.device("cpu")  # Export on CPU for compatibility

    # Load model
    model = build_model(num_classes=config.NUM_CLASSES, pretrained=False)
    state_dict = torch.load(checkpoint_path, map_location=device)
    model.load_state_dict(state_dict)
    model.eval()

    print(f"Loaded checkpoint: {checkpoint_path}")
    total_params = sum(p.numel() for p in model.parameters())
    print(f"Parameters: {total_params:,}")

    # Dummy input for tracing
    dummy_input = torch.randn(1, 3, config.IMAGE_SIZE, config.IMAGE_SIZE)

    # Export to ONNX
    print(f"Exporting to {output_path}...")
    torch.onnx.export(
        model,
        dummy_input,
        output_path,
        export_params=True,
        opset_version=18,
        do_constant_folding=True,
        input_names=["image"],
        output_names=["logits"],
        dynamic_axes={
            "image": {0: "batch_size"},
            "logits": {0: "batch_size"},
        },
    )

    # Verify the exported model
    onnx_model = onnx.load(output_path)
    onnx.checker.check_model(onnx_model)
    print("ONNX model verified successfully")

    # Print model size
    size_mb = os.path.getsize(output_path) / (1024 * 1024)
    print(f"Model size: {size_mb:.1f} MB")

    # Save metadata alongside the ONNX model
    meta_path = output_path.replace(".onnx", "_meta.json")
    meta = {
        "model_name": config.MODEL_NAME,
        "image_size": config.IMAGE_SIZE,
        "num_classes": config.NUM_CLASSES,
        "class_names": ["DOWN", "FLAT", "UP"],
        "window_snapshots": config.WINDOW_SNAPSHOTS,
        "horizon_seconds": config.HORIZON_SECONDS,
        "normalization": {
            "mean": [0.485, 0.456, 0.406],
            "std": [0.229, 0.224, 0.225],
        },
        "exported_at": str(np.datetime64("now")),
        "checkpoint": os.path.basename(checkpoint_path),
        "total_params": total_params,
    }

    with open(meta_path, "w") as f:
        json.dump(meta, f, indent=2)
    print(f"Metadata saved to {meta_path}")


def verify_onnx(onnx_path: str):
    """Run a test inference with ONNX Runtime to verify the model works."""
    import onnxruntime as ort

    session = ort.InferenceSession(onnx_path, providers=["CUDAExecutionProvider", "CPUExecutionProvider"])

    # Test with random input
    dummy = np.random.randn(1, 3, config.IMAGE_SIZE, config.IMAGE_SIZE).astype(np.float32)
    result = session.run(None, {"image": dummy})

    logits = result[0]
    probs = _softmax(logits[0])

    print(f"\nTest inference:")
    print(f"  Input shape:  (1, 3, {config.IMAGE_SIZE}, {config.IMAGE_SIZE})")
    print(f"  Output shape: {logits.shape}")
    print(f"  Probabilities: DOWN={probs[0]:.3f} FLAT={probs[1]:.3f} UP={probs[2]:.3f}")
    print(f"  Prediction:   {['DOWN', 'FLAT', 'UP'][np.argmax(probs)]}")

    # Measure inference time
    import time
    times = []
    for _ in range(100):
        t0 = time.perf_counter()
        session.run(None, {"image": dummy})
        times.append(time.perf_counter() - t0)

    avg_ms = np.mean(times) * 1000
    p95_ms = np.percentile(times, 95) * 1000
    print(f"  Inference: avg={avg_ms:.1f}ms, p95={p95_ms:.1f}ms")


def _softmax(x):
    e = np.exp(x - np.max(x))
    return e / e.sum()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--checkpoint", type=str, default=os.path.join(config.MODEL_DIR, "best.pt"))
    parser.add_argument("--output", type=str, default=config.ONNX_PATH)
    args = parser.parse_args()

    export_onnx(args.checkpoint, args.output)
    verify_onnx(args.output)
