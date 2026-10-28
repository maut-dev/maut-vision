"""Проверка архитектуры по самим весам.

Имя файла ничего не доказывает: yolo12n.pt может оказаться переименованной
yolo26n, а обучить «втихую» чужую сеть — худший исход (откатов на соседние
версии намеренно нет). Смотрим на конфиг, который
ultralytics сохраняет внутри чекпоинта: yaml_file и scale. Проверка идёт
дважды — у исходных весов до обучения и у best.pt после.
"""
from __future__ import annotations

from pathlib import Path
from typing import Any

from worker.protocol import DetectedArchitecture, ErrorCode, WorkerError
from worker.model_catalog import MODELS

def detect(model: Any) -> DetectedArchitecture:
    """Архитектура загруженной ultralytics.YOLO: например yolov12n.yaml, n, detect."""
    y = getattr(getattr(model, "model", None), "yaml", None) or {}
    # Ранние официальные YOLOv8 PT не содержат yaml_file. Их исходная
    # архитектура сохранена в train_args.model самого чекпоинта.
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
    # ultralytics пишет конфиг как yolov12n.yaml: «v» снимаем
    stem = Path(found.yaml_file).stem.lower().replace("yolov", "yolo")
    if found.task != definition["task"]:
        return False
    if stem in definition["yamlNames"]:
        # имя конфига уже содержит масштаб; противоречащий scale — отказ
        return found.scale in ("", scale)
    # общий конфиг семейства (yolo12.yaml) — масштаб только из поля scale
    return stem in definition["yamlFamilies"] and found.scale == scale


def verify(claimed: str, model: Any, where: str) -> DetectedArchitecture:
    found = detect(model)
    if not matches(claimed, found):
        raise WorkerError(
            ErrorCode.ARCHITECTURE_MISMATCH,
            f"{where}: заявлена {claimed}, а в весах записана '{found.yaml_file or '?'}' "
            f"(масштаб '{found.scale or '?'}', задача '{found.task or '?'}'). Обучение не запускалось.",
            {"where": where, "claimed": claimed, "found": found.model_dump(by_alias=True)},
        )
    return found
