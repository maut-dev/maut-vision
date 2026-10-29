"""The pinned detect model catalog shared with the application (catalog/yolo-catalog.json).

The host passes the path in MAUT_MODEL_CATALOG — the same file Core reads. Without the variable:
in the packaged worker — the copy next to it (_MEIPASS), from sources — catalog/ of the repository.
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
    # The application catalog is an array of entries; the former format kept them in "models".
    entries = raw["models"] if isinstance(raw, dict) else raw
    return {model["id"]: model for model in entries}


MODELS = _load()
SUPPORTED_ARCHITECTURES = tuple(MODELS)


def supported_architecture(value: str) -> str:
    if value not in MODELS:
        raise ValueError("The architecture is missing from the detect model catalog")
    return value
