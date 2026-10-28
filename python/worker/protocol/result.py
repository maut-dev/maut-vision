"""Schema of result.json.

The result is published once, atomically, when the artifacts are already in place
(runtime/job_layout.py). Artifact paths are relative to outputRoot:
the host itself checks that they do not leave it and
compares sha256 before registering the model.
"""
from __future__ import annotations

from enum import StrEnum
from typing import Any

from pydantic import BaseModel, ConfigDict, Field
from pydantic.alias_generators import to_camel

from worker.protocol.errors import ErrorCode


class _ResultModel(BaseModel):
    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True, extra="forbid")


class ResultStatus(StrEnum):
    SUCCEEDED = "succeeded"
    FAILED = "failed"


class ArtifactKind(StrEnum):
    WEIGHTS_PT = "weights_pt"
    ONNX = "onnx"


class ReportKind(StrEnum):
    """Training report files: not weights, they are not part of the model package."""

    PLOT = "plot"
    LOG = "log"


class ResultError(_ResultModel):
    """code — the protocol code; reason and args — the key and values of the text the UI shows (protocol 1, optional); message — English."""

    code: ErrorCode
    message: str
    details: dict[str, Any] = Field(default_factory=dict)
    reason: str | None = None
    args: dict[str, str] = Field(default_factory=dict)


class ResultWarning(_ResultModel):
    """A warning: code, optional reason and args of its text (worker/protocol/messages.py), the English message."""

    code: str
    message: str
    reason: str | None = None
    args: dict[str, str] = Field(default_factory=dict)


class Artifact(_ResultModel):
    kind: ArtifactKind
    path: str
    size_bytes: int
    sha256: str


class ReportFile(_ResultModel):
    """An ultralytics image (plot) or the training console log (log) in outputRoot.

    Checked the same way as an artifact: path inside outputRoot, sha256 and size.
    """

    kind: ReportKind
    path: str
    size_bytes: int
    sha256: str


class DetectedArchitecture(_ResultModel):
    """What is recorded in the weights themselves — not the file name but the config from the checkpoint."""

    yaml_file: str
    scale: str
    task: str


class EpochHistoryFile(_ResultModel):
    path: str
    size_bytes: int
    sha256: str
    count: int


class EpochRecord(_ResultModel):
    """A training epoch. losses — train losses (box_loss, cls_loss, dfl_loss) from
    trainer.tloss; metrics — quality on val (precision, recall, mAP50,
    mAP50_95) and val losses (valBoxLoss, valClsLoss, valDflLoss) from
    trainer.metrics — both sources are read in the on_fit_epoch_end callback."""

    epoch: int
    losses: dict[str, float]
    metrics: dict[str, float]
    lr: float | None = None
    # ultralytics fitness of this epoch: the trainer picks best.pt by its maximum
    fitness: float | None = None


class ClassMetrics(_ResultModel):
    """Metrics of one class of the evaluate operation; index — the model output."""

    index: int
    name: str
    metrics: dict[str, float]


class DetectionBox(_ResultModel):
    """A box as fractions of the analyzed frame (the cut-out, if region is set): 0..1."""

    x: float
    y: float
    width: float
    height: float


class Detection(_ResultModel):
    class_index: int
    name: str
    confidence: float
    box: DetectionBox


class PredictedFrame(_ResultModel):
    """A frame with detections. index — video frame number (0 for an image), timeMs — its time."""

    index: int
    time_ms: int
    detections: list[Detection]


class ClassSummary(_ResultModel):
    index: int
    name: str
    frames: int
    detections: int
    max_confidence: float


class PredictionSummary(_ResultModel):
    frames_processed: int
    frames_with_detections: int
    detections: int
    classes: list[ClassSummary] = Field(default_factory=list)


class SourceInfo(_ResultModel):
    """What the worker saw in the input: frame size, video duration and fps."""

    width: int
    height: int
    duration_ms: int | None = None
    fps: float | None = None


class MonitorInfo(_ResultModel):
    index: int
    left: int
    top: int
    width: int
    height: int


class Screenshot(_ResultModel):
    """A monitor screenshot in outputRoot (png), checked like an artifact: path, size, sha256."""

    path: str
    size_bytes: int
    sha256: str
    width: int
    height: int
    monitor: int
    monitors: list[MonitorInfo]


class JobResult(_ResultModel):
    protocol_version: int
    # None only if request.json was not read: there is nothing to link the result to
    operation_id: str | None
    operation: str | None
    status: ResultStatus
    started_at: str
    finished_at: str
    duration_sec: float
    error: ResultError | None = None
    warnings: list[ResultWarning] = Field(default_factory=list)
    architecture: DetectedArchitecture | None = None
    # the parameters actually applied, including the computed lr0 and warmup
    effective_params: dict[str, Any] | None = None
    metrics: dict[str, float] | None = None
    epochs_completed: int | None = None
    stopped_early: bool | None = None
    history: list[EpochRecord] = Field(default_factory=list)
    history_file: EpochHistoryFile | None = None
    # evaluate only: per-class metrics for classes that have objects in the split
    class_metrics: list[ClassMetrics] = Field(default_factory=list)
    artifacts: list[Artifact] = Field(default_factory=list)
    # train only: ultralytics images and the console log; an optional field of protocol v1
    reports: list[ReportFile] = Field(default_factory=list)
    environment: dict[str, Any] = Field(default_factory=dict)
    # predict/live only: run summary, input and (for an image) its single frame
    summary: PredictionSummary | None = None
    source: SourceInfo | None = None
    frames: list[PredictedFrame] = Field(default_factory=list)
    # screenshot only
    screenshot: Screenshot | None = None
