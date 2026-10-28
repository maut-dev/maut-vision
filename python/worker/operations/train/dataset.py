"""Preparing a YOLO dataset for training without changing the input.

input/ is immutable during a run, and data.yaml from Domain Datasets may
contain `path:` from another folder (the dataset was built in one place, the job
was materialized in another; so ultralytics might train on the wrong dataset
after a move). So data.yaml is not edited; a copy is written into
the job draft with `path:` pointing to the real dataset folder. The `download` key
is dropped: with it ultralytics downloads a dataset or runs a script.
"""
from __future__ import annotations

from pathlib import Path
from typing import Any

import yaml

from worker.protocol import ErrorCode, WorkerError

IMAGE_EXT = {".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tif", ".tiff"}


def _fail(reason: str, args: dict[str, Any] | None = None, **details: Any) -> WorkerError:
    """dataset_invalid with the reason of its message (worker/protocol/messages.py), its args and machine details."""
    return WorkerError(ErrorCode.DATASET_INVALID, reason, args, details)


def _split_paths(root: Path, value: Any, key: str) -> list[Path]:
    items = value if isinstance(value, list) else [value]
    out = []
    for item in items:
        if not isinstance(item, str) or not item:
            raise _fail("split_path_type", {"key": key}, key=key)
        p = (root / item).resolve()
        if not p.is_relative_to(root):
            raise _fail("split_outside", {"key": key}, key=key, value=item)
        if not p.exists():
            raise _fail("split_missing", {"key": key, "value": item}, key=key, value=item)
        out.append(p)
    return out


def _count_images(paths: list[Path]) -> int:
    n = 0
    for p in paths:
        if p.is_dir():
            n += sum(1 for f in p.rglob("*") if f.suffix.lower() in IMAGE_EXT)
        else:  # .txt with a list of images
            n += sum(1 for ln in p.read_text(encoding="utf-8").splitlines() if ln.strip())
    return n


def prepare(dataset_dir: Path, work_dir: Path) -> tuple[Path, dict[str, Any]]:
    """Return (path to the working data.yaml, dataset summary for result.json)."""
    src = dataset_dir / "data.yaml"
    if not src.is_file():
        raise _fail("yaml_missing", path="data.yaml")
    try:
        data = yaml.safe_load(src.read_text(encoding="utf-8"))
    except (yaml.YAMLError, UnicodeDecodeError) as exc:
        raise _fail("yaml_unreadable", {"error": exc}) from None
    if not isinstance(data, dict):
        raise _fail("yaml_not_mapping")

    names = data.get("names")
    if isinstance(names, dict):
        names = [names[k] for k in sorted(names)]
    if not isinstance(names, list) or not names or not all(isinstance(n, str) for n in names):
        raise _fail("names_invalid")
    if "nc" in data and data["nc"] != len(names):
        raise _fail("nc_mismatch", {"nc": data["nc"], "names": len(names)})

    root = dataset_dir.resolve()
    summary: dict[str, Any] = {"classes": len(names)}
    for key in ("train", "val"):
        if key not in data:
            raise _fail("key_missing", {"key": key}, key=key)
        count = _count_images(_split_paths(root, data[key], key))
        if count == 0:
            raise _fail("split_empty", {"key": key}, key=key)
        summary[f"images{key.capitalize()}"] = count
    if data.get("test"):
        # test is not required for training, but weights can be checked on it (evaluate)
        summary["imagesTest"] = _count_images(_split_paths(root, data["test"], "test"))

    prepared = {k: v for k, v in data.items() if k not in ("path", "download")}
    prepared["path"] = str(root)
    prepared["names"] = names
    prepared["nc"] = len(names)
    work_dir.mkdir(parents=True, exist_ok=True)
    out = work_dir / "data.yaml"
    out.write_text(yaml.safe_dump(prepared, allow_unicode=True, sort_keys=False), encoding="utf-8")
    return out, summary


def select_training_validation_split(data_yaml: Path, split: str, summary: dict[str, Any]) -> None:
    """DetectTrainer uses data['val'] for its supplied loader, independent of args.split.

    Point only the working YAML at the chosen immutable split. Never rewrite input data.
    """
    if split == "val":
        return
    count = summary.get("imagesTrain" if split == "train" else "imagesTest", 0)
    if count < 1:
        raise _fail("validation_split_empty", {"key": split}, key=split)
    prepared = yaml.safe_load(data_yaml.read_text(encoding="utf-8"))
    prepared["val"] = prepared[split]
    data_yaml.write_text(yaml.safe_dump(prepared, allow_unicode=True, sort_keys=False), encoding="utf-8")
