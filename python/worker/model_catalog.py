"""Общий с приложением закреплённый каталог моделей detect (catalog/yolo-catalog.json).

Путь передаёт host в MAUT_MODEL_CATALOG — тот же файл, что читает Core. Без переменной:
в собранном worker — копия рядом с ним (_MEIPASS), из исходников — catalog/ репозитория.
"""
from __future__ import annotations

import json
import os
import sys
from pathlib import Path


def _catalog_path() -> Path:
    configured = os.environ.get("MAUT_MODEL_CATALOG")
    if configured:
        return Path(configured)
    if getattr(sys, "frozen", False):
        return Path(sys._MEIPASS) / "yolo-catalog.json"
    return Path(__file__).resolve().parents[2] / "catalog" / "yolo-catalog.json"


def _load() -> dict[str, dict]:
    raw = json.loads(_catalog_path().read_text(encoding="utf-8"))
    # Каталог приложения — массив записей; прежний формат хранил их в "models".
    entries = raw["models"] if isinstance(raw, dict) else raw
    return {model["id"]: model for model in entries}


MODELS = _load()
SUPPORTED_ARCHITECTURES = tuple(MODELS)


def supported_architecture(value: str) -> str:
    if value not in MODELS:
        raise ValueError("Архитектура отсутствует в каталоге моделей detect")
    return value
