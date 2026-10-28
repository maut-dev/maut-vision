"""The common part of predict/live checks: loading weights, running a frame, preview, summary.

A frame is numpy BGR (as in OpenCV and as ultralytics expects for an ndarray). Boxes
are returned as fractions of the analyzed frame, so they can be drawn over
a reduced preview without recalculation.
"""
from __future__ import annotations

import os
import sys
import time
import traceback
from pathlib import Path
from typing import Any

from worker.operations.context import OperationContext
from worker.operations.predict.nms_policy import complete_nms
from worker.operations.train import architecture, ultralytics_setup
from worker.operations.train.run import _check_device, _load_model
from worker.protocol import (
    ClassSummary,
    Detection,
    DetectionBox,
    ErrorCode,
    PredictedFrame,
    PredictionSummary,
    Region,
    WorkerError,
)

# Preview — a reduced JPEG copy of the frame, as a file in output/previews/: the host reads
# and deletes it; the bytes do not go through events and the bridge.
PREVIEW_DIR = "previews"


def load(ctx: OperationContext, model_path: Path, claimed: str, device: str) -> tuple[Any, str | int, dict[int, str]]:
    """Version weights → YOLO; the architecture is checked against the checkpoint, as in evaluate."""
    ultralytics_setup.configure(ctx.layout.work_dir)
    checked = _check_device(device)
    ctx.stage("loading_model")
    model = _load_model(model_path)
    architecture.verify(claimed, model, "version_weights")
    raw = getattr(model, "names", None) or {}
    names = {int(k): str(v) for k, v in raw.items()} if isinstance(raw, dict) else {i: str(n) for i, n in enumerate(raw)}
    return model, checked, names


def crop(frame: Any, region: Region | None, where: str) -> Any:
    """Crop cut-out. A crop outside the frame is a refusal, not a silent clip:
    the model is trained on frames of exactly this size."""
    if region is None:
        return frame
    height, width = frame.shape[:2]
    if region.x + region.width > width or region.y + region.height > height:
        raise WorkerError(
            ErrorCode.REGION_INVALID,
            f"outside_{where}",
            {"width": region.width, "height": region.height, "x": region.x, "y": region.y, "frameWidth": width, "frameHeight": height},
            {"frame": {"width": width, "height": height}},
        )
    return frame[region.y : region.y + region.height, region.x : region.x + region.width]


def detect(model: Any, frame: Any, *, imgsz: int, conf: float, device: str | int, max_det: int | None, names: dict[int, str]) -> list[Detection]:
    try:
        with complete_nms(max_det, imgsz):
            results = model.predict(frame, imgsz=imgsz, conf=conf, device=device, max_det=max_det if max_det is not None else imgsz * imgsz, verbose=False)
    except Exception as exc:  # noqa: BLE001 — an inference failure is described in result.json
        traceback.print_exc(file=sys.stderr)
        oom = "out of memory" in str(exc).lower()
        raise WorkerError(
            ErrorCode.OUT_OF_MEMORY if oom else ErrorCode.PREDICTION_FAILED,
            "predict" if oom else None,
            {"error": f"{type(exc).__name__}: {exc}"[:1000]},
        ) from None
    boxes = getattr(results[0], "boxes", None) if results else None
    if boxes is None or len(boxes) == 0:
        return []
    xyxyn = boxes.xyxyn.cpu().numpy()
    confidences = boxes.conf.cpu().numpy()
    classes = boxes.cls.cpu().numpy().astype(int)
    out: list[Detection] = []
    for (x1, y1, x2, y2), confidence, index in zip(xyxyn, confidences, classes):
        left, top = max(0.0, float(x1)), max(0.0, float(y1))
        right, bottom = min(1.0, float(x2)), min(1.0, float(y2))
        out.append(
            Detection(
                class_index=int(index),
                name=names.get(int(index), str(int(index))),
                confidence=round(float(confidence), 4),
                box=DetectionBox(
                    x=round(left, 5), y=round(top, 5), width=round(max(0.0, right - left), 5), height=round(max(0.0, bottom - top), 5)
                ),
            )
        )
    out.sort(key=lambda item: -item.confidence)
    return out


def write_preview(ctx: OperationContext, frame: Any, index: int, max_side: int = 960) -> str | None:
    """JPEG q80 in output/previews/frame-<index>.jpg (written via a temporary file and
    a rename: the host never reads half of it). Returns the path relative to outputRoot
    for the previewFile field; if encoding failed, there is no preview."""
    import cv2

    height, width = frame.shape[:2]
    scale = min(1.0, max_side / max(height, width))
    if scale < 1.0:
        frame = cv2.resize(frame, (max(1, round(width * scale)), max(1, round(height * scale))), interpolation=cv2.INTER_AREA)
    ok, buffer = cv2.imencode(".jpg", frame, [int(cv2.IMWRITE_JPEG_QUALITY), 80])
    if not ok:
        return None
    folder = ctx.layout.output_dir / PREVIEW_DIR
    folder.mkdir(parents=True, exist_ok=True)
    name = f"frame-{index}.jpg"
    temporary = folder / f".{name}.tmp"
    temporary.write_bytes(buffer.tobytes())
    os.replace(temporary, folder / name)
    return f"{PREVIEW_DIR}/{name}"


class Summary:
    """Per-class run summary: in how many frames, how many boxes, the maximum confidence."""

    def __init__(self) -> None:
        self.frames_processed = 0
        self.frames_with_detections = 0
        self.detections = 0
        self._classes: dict[int, dict[str, Any]] = {}

    def add(self, detections: list[Detection]) -> None:
        self.frames_processed += 1
        if not detections:
            return
        self.frames_with_detections += 1
        self.detections += len(detections)
        seen: set[int] = set()
        for item in detections:
            entry = self._classes.setdefault(item.class_index, {"name": item.name, "frames": 0, "detections": 0, "max": 0.0})
            entry["detections"] += 1
            entry["max"] = max(entry["max"], item.confidence)
            if item.class_index not in seen:
                seen.add(item.class_index)
                entry["frames"] += 1

    def model(self) -> PredictionSummary:
        return PredictionSummary(
            frames_processed=self.frames_processed,
            frames_with_detections=self.frames_with_detections,
            detections=self.detections,
            classes=[
                ClassSummary(index=index, name=entry["name"], frames=entry["frames"], detections=entry["detections"], max_confidence=entry["max"])
                for index, entry in sorted(self._classes.items())
            ],
        )


class PreviewClock:
    """Preview no more often than the interval: large event lines must not clog the pipe."""

    def __init__(self, interval_ms: int):
        self._interval = interval_ms / 1000
        self._last = float("-inf")

    def due(self) -> bool:
        now = time.monotonic()
        if now - self._last >= self._interval:
            self._last = now
            return True
        return False


def emit_frame(ctx: OperationContext, frame: PredictedFrame, width: int, height: int, preview_file: str | None, inference_ms: int) -> None:
    payload = frame.model_dump(mode="json", by_alias=True)
    fields: dict[str, Any] = {**payload, "width": width, "height": height, "inferenceMs": inference_ms}
    if preview_file is not None:
        fields["previewFile"] = preview_file
    ctx.events.emit("frame", **fields)
