"""Build a fixture job folder for checking the worker by hand.

The dataset is synthetic: 3 shape classes (square, circle, triangle) on a noisy
background, 30 frames (24 train / 6 val). Only for checking the protocol — the model
will learn nothing meaningful from it.

  python tools/make_fixture_job.py <job folder> --weights <base.pt> [--epochs 2] [--device cpu]
"""
from __future__ import annotations

import argparse
import json
import random
import shutil
from pathlib import Path

from PIL import Image, ImageDraw

CLASSES = ["square", "circle", "triangle"]
SIZE = 128


def _frame(rng: random.Random) -> tuple[Image.Image, list[str]]:
    img = Image.effect_noise((SIZE, SIZE), 40).convert("RGB")
    draw = ImageDraw.Draw(img)
    labels = []
    for _ in range(rng.randint(1, 3)):
        cls = rng.randrange(len(CLASSES))
        w = rng.randint(20, 40)
        x0 = rng.randint(0, SIZE - w)
        y0 = rng.randint(0, SIZE - w)
        color = tuple(rng.randint(80, 255) for _ in range(3))
        box = (x0, y0, x0 + w, y0 + w)
        if cls == 0:
            draw.rectangle(box, fill=color)
        elif cls == 1:
            draw.ellipse(box, fill=color)
        else:
            draw.polygon([(x0 + w / 2, y0), (x0, y0 + w), (x0 + w, y0 + w)], fill=color)
        labels.append(f"{cls} {(x0 + w / 2) / SIZE:.6f} {(y0 + w / 2) / SIZE:.6f} {w / SIZE:.6f} {w / SIZE:.6f}")
    return img, labels


def build(job: Path, weights: Path, epochs: int, device: str, frames: int = 30) -> None:
    rng = random.Random(0)
    ds = job / "input" / "dataset"
    for i in range(frames):
        split = "val" if i % 5 == 0 else "train"
        (ds / "images" / split).mkdir(parents=True, exist_ok=True)
        (ds / "labels" / split).mkdir(parents=True, exist_ok=True)
        img, labels = _frame(rng)
        img.save(ds / "images" / split / f"f{i:03d}.png")
        (ds / "labels" / split / f"f{i:03d}.txt").write_text("\n".join(labels) + "\n", encoding="utf-8")
    # path is deliberately foreign: the worker must substitute the real folder itself
    (ds / "data.yaml").write_text(
        "path: C:/somewhere/else\ntrain: images/train\nval: images/val\n"
        f"nc: {len(CLASSES)}\nnames: {json.dumps(CLASSES)}\n",
        encoding="utf-8",
    )
    model_dir = job / "input" / "model"
    model_dir.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(weights, model_dir / weights.name)
    (job / "input" / "manifest.json").write_text(json.dumps({"fixture": True}), encoding="utf-8")
    (job / "output").mkdir(exist_ok=True)
    (job / "logs").mkdir(exist_ok=True)
    request = {
        "protocolVersion": 1,
        "operationId": f"fixture-{job.name}",
        "operation": "train",
        "input": {
            "dataset": "input/dataset",
            "model": f"input/model/{weights.name}",
            "manifest": "input/manifest.json",
        },
        "outputRoot": "output",
        "params": {
            "architecture": weights.stem,
            "epochs": epochs,
            "batch": 8,
            "imgsz": SIZE,
            "device": device,
            "seed": 0,
            "exportOnnx": True,
        },
    }
    (job / "request.json").write_text(json.dumps(request, indent=2), encoding="utf-8")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("job", type=Path)
    ap.add_argument("--weights", type=Path, required=True)
    ap.add_argument("--epochs", type=int, default=2)
    ap.add_argument("--device", default="cpu")
    a = ap.parse_args()
    if a.job.exists():
        shutil.rmtree(a.job)
    build(a.job, a.weights, a.epochs, a.device)
    print(a.job)


if __name__ == "__main__":
    main()
