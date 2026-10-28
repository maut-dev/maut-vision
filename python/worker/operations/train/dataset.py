"""Подготовка датасета YOLO к обучению без изменения входа.

input/ неизменяем во время запуска, а data.yaml от Domain Datasets может
содержать `path:` из другой папки (датасет собирали в одном месте, задачу
материализовали в другом; так ultralytics может учиться не на том датасете
после переноса). Поэтому data.yaml не правим, а пишем копию в
черновик задачи с `path:` на настоящую папку датасета. Ключ `download`
выбрасываем: по нему ultralytics скачивает датасет или исполняет скрипт.
"""
from __future__ import annotations

from pathlib import Path
from typing import Any

import yaml

from worker.protocol import ErrorCode, WorkerError

IMAGE_EXT = {".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tif", ".tiff"}


def _fail(message: str, **details: Any) -> WorkerError:
    return WorkerError(ErrorCode.DATASET_INVALID, message, details)


def _split_paths(root: Path, value: Any, key: str) -> list[Path]:
    items = value if isinstance(value, list) else [value]
    out = []
    for item in items:
        if not isinstance(item, str) or not item:
            raise _fail(f"data.yaml: '{key}' должен быть путём или списком путей", key=key)
        p = (root / item).resolve()
        if not p.is_relative_to(root):
            raise _fail(f"data.yaml: '{key}' указывает за пределы датасета", key=key, value=item)
        if not p.exists():
            raise _fail(f"В датасете нет '{item}' (ключ '{key}' в data.yaml)", key=key, value=item)
        out.append(p)
    return out


def _count_images(paths: list[Path]) -> int:
    n = 0
    for p in paths:
        if p.is_dir():
            n += sum(1 for f in p.rglob("*") if f.suffix.lower() in IMAGE_EXT)
        else:  # .txt со списком картинок
            n += sum(1 for ln in p.read_text(encoding="utf-8").splitlines() if ln.strip())
    return n


def prepare(dataset_dir: Path, work_dir: Path) -> tuple[Path, dict[str, Any]]:
    """Вернуть (путь к рабочему data.yaml, сводка датасета для result.json)."""
    src = dataset_dir / "data.yaml"
    if not src.is_file():
        raise _fail("В input/dataset нет data.yaml", path="data.yaml")
    try:
        data = yaml.safe_load(src.read_text(encoding="utf-8"))
    except (yaml.YAMLError, UnicodeDecodeError) as exc:
        raise _fail(f"data.yaml не читается: {exc}") from None
    if not isinstance(data, dict):
        raise _fail("data.yaml должен быть словарём")

    names = data.get("names")
    if isinstance(names, dict):
        names = [names[k] for k in sorted(names)]
    if not isinstance(names, list) or not names or not all(isinstance(n, str) for n in names):
        raise _fail("data.yaml: 'names' должен быть непустым списком имён классов")
    if "nc" in data and data["nc"] != len(names):
        raise _fail(f"data.yaml: nc={data['nc']}, а имён классов {len(names)}")

    root = dataset_dir.resolve()
    summary: dict[str, Any] = {"classes": len(names)}
    for key in ("train", "val"):
        if key not in data:
            raise _fail(f"data.yaml: нет ключа '{key}'", key=key)
        count = _count_images(_split_paths(root, data[key], key))
        if count == 0:
            raise _fail(f"В выборке '{key}' нет ни одной картинки", key=key)
        summary[f"images{key.capitalize()}"] = count
    if data.get("test"):
        # test не обязателен для обучения, но по нему можно проверять веса (evaluate)
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
        raise _fail(f"В выбранной проверочной выборке '{split}' нет изображений", key=split)
    prepared = yaml.safe_load(data_yaml.read_text(encoding="utf-8"))
    prepared["val"] = prepared[split]
    data_yaml.write_text(yaml.safe_dump(prepared, allow_unicode=True, sort_keys=False), encoding="utf-8")
