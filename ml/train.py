"""
Step 3: Fine-tune Swin-Tiny on L2 order book heatmaps.

Two-phase training:
  Phase 1: Freeze backbone, train classification head only (fast convergence)
  Phase 2: Unfreeze top layers, lower learning rate with warmup + cosine decay

Usage:
  python train.py                          # full training
  python train.py --head-only              # phase 1 only (quick test)
  python train.py --resume models/best.pt  # resume from checkpoint
"""

import argparse
import json
import os
import random
import time

import numpy as np
import torch
import torch.nn as nn
from sklearn.utils.class_weight import compute_class_weight
from torch.utils.data import DataLoader, Dataset, WeightedRandomSampler
from torchvision import transforms
from tqdm import tqdm

import config

# Use timm for Swin-Tiny — it has the cleanest implementation
import timm


# ── Reproducibility ──────────────────────────────────────────────────
def set_seed(seed: int = config.SEED):
    random.seed(seed)
    np.random.seed(seed)
    torch.manual_seed(seed)
    if torch.cuda.is_available():
        torch.cuda.manual_seed_all(seed)


def load_channel_stats() -> tuple[list[float], list[float]]:
    """
    Load per-channel normalization stats computed from actual L2 heatmaps.
    Falls back to ImageNet stats only if channel_stats.json doesn't exist
    (first-ever training run before render_heatmap.py has been run).
    """
    stats_path = config.CHANNEL_STATS_PATH
    if os.path.exists(stats_path):
        with open(stats_path, "r") as f:
            stats = json.load(f)
        print(f"Using L2 heatmap channel stats (from {stats['num_samples']} samples)")
        return stats["mean"], stats["std"]
    else:
        print("WARNING: channel_stats.json not found, falling back to ImageNet stats.")
        print("  Run render_heatmap.py first to compute actual L2 channel statistics.")
        return [0.485, 0.456, 0.406], [0.229, 0.224, 0.225]


class HeatmapDataset(Dataset):
    """Dataset of rendered L2 heatmap images with UP/FLAT/DOWN labels."""

    def __init__(self, images: np.ndarray, labels: np.ndarray, transform=None):
        self.images = images    # (N, 224, 224, 3) uint8
        self.labels = labels    # (N,) int64
        self.transform = transform

    def __len__(self):
        return len(self.labels)

    def __getitem__(self, idx):
        img = self.images[idx]  # (224, 224, 3) uint8

        # Convert to PIL for torchvision transforms
        from PIL import Image
        img = Image.fromarray(img)

        if self.transform:
            img = self.transform(img)
        else:
            img = transforms.ToTensor()(img)

        return img, self.labels[idx]


def build_model(num_classes: int = config.NUM_CLASSES, pretrained: bool = True) -> nn.Module:
    """Build Swin-Tiny with custom classification head."""
    model = timm.create_model(
        "swin_tiny_patch4_window7_224",
        pretrained=pretrained,
        num_classes=num_classes,
    )
    return model


def freeze_backbone(model: nn.Module):
    """Freeze all layers except the classification head."""
    for name, param in model.named_parameters():
        if "head" not in name:
            param.requires_grad = False


def unfreeze_top_layers(model: nn.Module, unfreeze_stages: int = 2):
    """Unfreeze the last N stages + classification head."""
    # Swin has layers.0, layers.1, layers.2, layers.3 (4 stages)
    # Unfreeze from the end
    stage_start = 4 - unfreeze_stages  # e.g., unfreeze_stages=2 -> unfreeze layers.2 and layers.3
    for name, param in model.named_parameters():
        if "head" in name:
            param.requires_grad = True
        elif "layers." in name:
            # Extract stage index: "layers.X...."
            parts = name.split(".")
            layer_idx = int(parts[1]) if len(parts) > 1 and parts[1].isdigit() else -1
            if layer_idx >= stage_start:
                param.requires_grad = True
            else:
                param.requires_grad = False
        elif "norm" in name:
            param.requires_grad = True  # always unfreeze final norm
        else:
            param.requires_grad = False

    trainable = sum(p.numel() for p in model.parameters() if p.requires_grad)
    total = sum(p.numel() for p in model.parameters())
    print(f"Trainable: {trainable:,} / {total:,} ({100*trainable/total:.1f}%)")


def get_transforms(train: bool, mean: list[float], std: list[float]):
    """Data augmentation for training, basic normalization for eval."""
    normalize = transforms.Normalize(mean=mean, std=std)

    if train:
        return transforms.Compose([
            transforms.RandomHorizontalFlip(p=0.0),   # NO horizontal flip — time flows left->right
            transforms.RandomVerticalFlip(p=0.0),     # NO vertical flip — price direction matters
            transforms.ColorJitter(brightness=0.2, contrast=0.2),  # Brightness/contrast augment
            transforms.RandomAffine(degrees=0, translate=(0.05, 0.05)),  # Small shifts
            transforms.ToTensor(),
            normalize,
        ])
    else:
        return transforms.Compose([
            transforms.ToTensor(),
            normalize,
        ])


def train_epoch(model, loader, criterion, optimizer, device, desc="Train"):
    model.train()
    total_loss = 0
    correct = 0
    total = 0

    pbar = tqdm(loader, desc=desc)
    for images, labels in pbar:
        images = images.to(device)
        labels = labels.to(device)

        optimizer.zero_grad()
        outputs = model(images)
        loss = criterion(outputs, labels)
        loss.backward()

        # Gradient clipping — prevents exploding gradients during fine-tuning
        torch.nn.utils.clip_grad_norm_(model.parameters(), max_norm=1.0)

        optimizer.step()

        total_loss += loss.item() * images.size(0)
        _, predicted = outputs.max(1)
        correct += predicted.eq(labels).sum().item()
        total += labels.size(0)

        pbar.set_postfix(loss=f"{loss.item():.4f}", acc=f"{100*correct/total:.1f}%")

    return total_loss / total, correct / total


@torch.no_grad()
def eval_epoch(model, loader, criterion, device, desc="Val"):
    model.eval()
    total_loss = 0
    correct = 0
    total = 0
    class_correct = [0] * config.NUM_CLASSES
    class_total = [0] * config.NUM_CLASSES

    for images, labels in tqdm(loader, desc=desc):
        images = images.to(device)
        labels = labels.to(device)

        outputs = model(images)
        loss = criterion(outputs, labels)

        total_loss += loss.item() * images.size(0)
        _, predicted = outputs.max(1)
        correct += predicted.eq(labels).sum().item()
        total += labels.size(0)

        for i in range(config.NUM_CLASSES):
            mask = labels == i
            class_correct[i] += predicted[mask].eq(labels[mask]).sum().item()
            class_total[i] += mask.sum().item()

    acc = correct / total
    class_acc = [
        class_correct[i] / max(class_total[i], 1)
        for i in range(config.NUM_CLASSES)
    ]

    print(f"  Overall acc: {100*acc:.1f}%")
    for i, name in enumerate(["DOWN", "FLAT", "UP"]):
        print(f"  {name}: {100*class_acc[i]:.1f}% ({class_correct[i]}/{class_total[i]})")

    return total_loss / total, acc


def get_cosine_with_warmup_scheduler(optimizer, warmup_steps: int, total_steps: int):
    """Linear warmup then cosine decay to eta_min."""
    def lr_lambda(current_step):
        if current_step < warmup_steps:
            return float(current_step) / float(max(1, warmup_steps))
        progress = float(current_step - warmup_steps) / float(max(1, total_steps - warmup_steps))
        return max(0.0, 0.5 * (1.0 + np.cos(np.pi * progress)))
    return torch.optim.lr_scheduler.LambdaLR(optimizer, lr_lambda)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--head-only", action="store_true", help="Phase 1 only")
    parser.add_argument("--resume", type=str, default=None, help="Resume from checkpoint")
    args = parser.parse_args()

    set_seed()

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"Device: {device}")
    if device.type == "cuda":
        print(f"GPU: {torch.cuda.get_device_name()}")
        print(f"VRAM: {torch.cuda.get_device_properties(0).total_memory / 1e9:.1f} GB")

    # ── Load channel statistics (from actual heatmaps, not ImageNet) ──
    channel_mean, channel_std = load_channel_stats()

    # ── Load data ─────────────────────────────────────────────────────
    print("Loading data...")
    images = np.load(os.path.join(config.DATA_DIR, "images.npy"))
    labels = np.load(os.path.join(config.DATA_DIR, "labels.npy"))
    print(f"Loaded {len(images)} samples")

    # Print class distribution
    unique, counts = np.unique(labels, return_counts=True)
    for u, c in zip(unique, counts):
        name = ["DOWN", "FLAT", "UP"][u]
        print(f"  {name}: {c} ({100*c/len(labels):.1f}%)")

    # ── Split: train / val / test ─────────────────────────────────────
    idx = np.arange(len(labels))
    # Time-ordered split: don't shuffle to avoid lookahead bias
    n_train = int(len(idx) * config.TRAIN_SPLIT)
    n_val = int(len(idx) * config.VAL_SPLIT)

    train_idx = idx[:n_train]
    val_idx = idx[n_train:n_train + n_val]
    test_idx = idx[n_train + n_val:]

    print(f"Train: {len(train_idx)}, Val: {len(val_idx)}, Test: {len(test_idx)}")

    # ── Class weights for imbalanced data ─────────────────────────────
    class_weights = compute_class_weight("balanced", classes=np.array([0, 1, 2]), y=labels[train_idx])
    class_weights = torch.tensor(class_weights, dtype=torch.float32).to(device)
    print(f"Class weights: DOWN={class_weights[0]:.2f} FLAT={class_weights[1]:.2f} UP={class_weights[2]:.2f}")

    # ── Datasets & loaders ────────────────────────────────────────────
    train_ds = HeatmapDataset(images[train_idx], labels[train_idx],
                              transform=get_transforms(train=True, mean=channel_mean, std=channel_std))
    val_ds = HeatmapDataset(images[val_idx], labels[val_idx],
                            transform=get_transforms(train=False, mean=channel_mean, std=channel_std))
    test_ds = HeatmapDataset(images[test_idx], labels[test_idx],
                             transform=get_transforms(train=False, mean=channel_mean, std=channel_std))

    # Weighted sampler to handle class imbalance
    sample_weights = class_weights[labels[train_idx]].cpu().numpy()
    sampler = WeightedRandomSampler(sample_weights, len(sample_weights))

    train_loader = DataLoader(train_ds, batch_size=config.BATCH_SIZE, sampler=sampler,
                              num_workers=4, pin_memory=True, persistent_workers=True)
    val_loader = DataLoader(val_ds, batch_size=config.BATCH_SIZE, shuffle=False,
                            num_workers=2, pin_memory=True, persistent_workers=True)
    test_loader = DataLoader(test_ds, batch_size=config.BATCH_SIZE, shuffle=False,
                             num_workers=2, pin_memory=True, persistent_workers=True)

    # ── Model ─────────────────────────────────────────────────────────
    model = build_model(num_classes=config.NUM_CLASSES, pretrained=True)

    if args.resume:
        print(f"Resuming from {args.resume}")
        model.load_state_dict(torch.load(args.resume, map_location=device))

    model = model.to(device)

    criterion = nn.CrossEntropyLoss(weight=class_weights)
    os.makedirs(config.MODEL_DIR, exist_ok=True)
    best_val_acc = 0.0

    # ── Phase 1: Train head only ──────────────────────────────────────
    print("\n" + "=" * 60)
    print("PHASE 1: Frozen backbone — training classification head")
    print("=" * 60)

    freeze_backbone(model)
    trainable = sum(p.numel() for p in model.parameters() if p.requires_grad)
    print(f"Trainable params: {trainable:,}")

    # Phase 1 LR: 1e-4 (was 1e-3 = way too high for a small head)
    optimizer = torch.optim.AdamW(
        filter(lambda p: p.requires_grad, model.parameters()),
        lr=config.LEARNING_RATE,
        weight_decay=config.WEIGHT_DECAY,
    )

    for epoch in range(config.EPOCHS_HEAD):
        print(f"\nEpoch {epoch+1}/{config.EPOCHS_HEAD}")
        t0 = time.time()
        train_loss, train_acc = train_epoch(model, train_loader, criterion, optimizer, device)
        val_loss, val_acc = eval_epoch(model, val_loader, criterion, device)

        elapsed = time.time() - t0
        print(f"  Train loss: {train_loss:.4f}, acc: {100*train_acc:.1f}%")
        print(f"  Val   loss: {val_loss:.4f}, acc: {100*val_acc:.1f}%")
        print(f"  Time: {elapsed:.0f}s")

        if val_acc > best_val_acc:
            best_val_acc = val_acc
            torch.save(model.state_dict(), os.path.join(config.MODEL_DIR, "best.pt"))
            print(f"  -> New best: {100*val_acc:.1f}%")

    if args.head_only:
        print("\n--head-only specified, skipping Phase 2")
    else:
        # ── Phase 2: Fine-tune top layers with warmup + cosine decay ──
        print("\n" + "=" * 60)
        print("PHASE 2: Unfreezing top 2 stages — fine-tuning with warmup")
        print("=" * 60)

        unfreeze_top_layers(model, unfreeze_stages=2)

        optimizer = torch.optim.AdamW(
            filter(lambda p: p.requires_grad, model.parameters()),
            lr=config.LEARNING_RATE,
            weight_decay=config.WEIGHT_DECAY,
        )

        # Warmup + cosine decay scheduler
        steps_per_epoch = len(train_loader)
        total_steps = config.EPOCHS_FINETUNE * steps_per_epoch
        warmup_steps = int(total_steps * config.WARMUP_RATIO)
        scheduler = get_cosine_with_warmup_scheduler(optimizer, warmup_steps, total_steps)
        print(f"Scheduler: {warmup_steps} warmup steps, {total_steps} total steps")

        patience = config.EARLY_STOPPING_PATIENCE
        no_improve = 0

        for epoch in range(config.EPOCHS_FINETUNE):
            print(f"\nEpoch {epoch+1}/{config.EPOCHS_FINETUNE} (LR={optimizer.param_groups[0]['lr']:.2e})")
            t0 = time.time()

            # Train with per-step scheduler updates
            model.train()
            total_loss = 0
            correct = 0
            total = 0
            pbar = tqdm(train_loader, desc="Train")
            for images_batch, labels_batch in pbar:
                images_batch = images_batch.to(device)
                labels_batch = labels_batch.to(device)

                optimizer.zero_grad()
                outputs = model(images_batch)
                loss = criterion(outputs, labels_batch)
                loss.backward()
                torch.nn.utils.clip_grad_norm_(model.parameters(), max_norm=1.0)
                optimizer.step()
                scheduler.step()  # Per-step update for warmup

                total_loss += loss.item() * images_batch.size(0)
                _, predicted = outputs.max(1)
                correct += predicted.eq(labels_batch).sum().item()
                total += labels_batch.size(0)
                pbar.set_postfix(loss=f"{loss.item():.4f}", acc=f"{100*correct/total:.1f}%",
                                 lr=f"{optimizer.param_groups[0]['lr']:.1e}")

            train_loss = total_loss / total
            train_acc = correct / total

            val_loss, val_acc = eval_epoch(model, val_loader, criterion, device)

            elapsed = time.time() - t0
            print(f"  Train loss: {train_loss:.4f}, acc: {100*train_acc:.1f}%")
            print(f"  Val   loss: {val_loss:.4f}, acc: {100*val_acc:.1f}%")
            print(f"  Time: {elapsed:.0f}s")

            if val_acc > best_val_acc:
                best_val_acc = val_acc
                no_improve = 0
                torch.save(model.state_dict(), os.path.join(config.MODEL_DIR, "best.pt"))
                print(f"  -> New best: {100*val_acc:.1f}%")
            else:
                no_improve += 1
                if no_improve >= patience:
                    print(f"  Early stopping after {patience} epochs without improvement")
                    break

    # ── Test set evaluation ───────────────────────────────────────────
    print("\n" + "=" * 60)
    print("TEST SET EVALUATION")
    print("=" * 60)

    model.load_state_dict(torch.load(os.path.join(config.MODEL_DIR, "best.pt"), map_location=device))
    test_loss, test_acc = eval_epoch(model, test_loader, criterion, device, desc="Test")
    print(f"\nTest accuracy: {100*test_acc:.1f}%")
    print(f"Best val accuracy: {100*best_val_acc:.1f}%")

    print(f"\nModel saved to {config.MODEL_DIR}/best.pt")
    print(f"Run export_onnx.py to convert for .NET integration")


if __name__ == "__main__":
    main()
