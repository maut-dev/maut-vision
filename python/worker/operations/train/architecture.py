"""Architecture check by the weights themselves.

The file name proves nothing: yolo12n.pt may turn out to be a renamed
yolo26n, and silently training someone else's network is the worst outcome (fallbacks to neighboring
versions deliberately do not exist). We look at the config that
ultralytics stores inside the checkpoint: yaml_file and scale. The check runs
twice — on the source weights before training and on best.pt after it.
"""
from __future__ import annotations

from pathlib import Path
from typing import Any

from worker.protocol import DetectedArchitecture, ErrorCode, WorkerError
from worker.model_catalog import MODELS

def detect(model: Any) -> DetectedArchitecture:
    """Architecture of a loaded ultralytics.YOLO: for example yolov12n.yaml, n, detect."""
    y = getattr(getattr(model, "model", None), "yaml", None) or {}
    # Early official YOLOv8 PT files do not contain yaml_file. Their source
    # architecture is stored in train_args.model of the checkpoint itself.
    checkpoint = getattr(model, "ckpt", None) or {}
    original = (checkpoint.get("train_args") or {}).get("model", "")
    return DetectedArchitecture(
        yaml_file=Path(str(y.get("yaml_file") or original or "")).name,
        scale=str(y.get("scale") or ""),
        task=str(getattr(model, "task", "") or ""),
    )


def matches(claimed: str, found: DetectedArchitecture) -> bool:
    definition = MODELS.get(claimed)
    if definition is None:
        return False
    scale = definition["scale"]
    # ultralytics writes the config as yolov12n.yaml: drop the "v"
    stem = Path(found.yaml_file).stem.lower().replace("yolov", "yolo")
    if found.task != definition["task"]:
        return False
    if stem in definition["yamlNames"]:
        # the config name already contains the scale; a contradicting scale is a refusal
        return found.scale in ("", scale)
    # a common family config (yolo12.yaml) — the scale comes only from the scale field
    return stem in definition["yamlFamilies"] and found.scale == scale


def verify(claimed: str, model: Any, where: str) -> DetectedArchitecture:
    found = detect(model)
    if not matches(claimed, found):
        raise WorkerError(
            ErrorCode.ARCHITECTURE_MISMATCH,
            where,
            {"claimed": claimed, "yamlFile": found.yaml_file or "?", "scale": found.scale or "?", "task": found.task or "?"},
            {"where": where, "claimed": claimed, "found": found.model_dump(by_alias=True)},
        )
    return found
