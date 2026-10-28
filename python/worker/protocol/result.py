"""Схема result.json.

Результат публикуется один раз, атомарно, когда артефакты уже на месте
(runtime/job_layout.py). Пути артефактов — относительно outputRoot:
host сам проверяет, что они не выходят за его пределы, и
сверяет sha256 перед регистрацией модели.
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
    """Отчётные файлы обучения: не веса, в пакет модели не входят."""

    PLOT = "plot"
    LOG = "log"


class ResultError(_ResultModel):
    code: ErrorCode
    message: str
    details: dict[str, Any] = Field(default_factory=dict)


class ResultWarning(_ResultModel):
    code: str
    message: str


class Artifact(_ResultModel):
    kind: ArtifactKind
    path: str
    size_bytes: int
    sha256: str


class ReportFile(_ResultModel):
    """Картинка ultralytics (plot) или консольный лог обучения (log) в outputRoot.

    Проверяется так же, как артефакт: путь внутри outputRoot, sha256 и размер.
    """

    kind: ReportKind
    path: str
    size_bytes: int
    sha256: str


class DetectedArchitecture(_ResultModel):
    """Что записано в самих весах — не имя файла, а конфиг из чекпоинта."""

    yaml_file: str
    scale: str
    task: str


class EpochHistoryFile(_ResultModel):
    path: str
    size_bytes: int
    sha256: str
    count: int


class EpochRecord(_ResultModel):
    """Эпоха обучения. losses — потери train (box_loss, cls_loss, dfl_loss) из
    trainer.tloss; metrics — качество на val (precision, recall, mAP50,
    mAP50_95) и потери val (valBoxLoss, valClsLoss, valDflLoss) из
    trainer.metrics — оба источника читаются в коллбэке on_fit_epoch_end."""

    epoch: int
    losses: dict[str, float]
    metrics: dict[str, float]
    lr: float | None = None
    # fitness ultralytics этой эпохи: по её максимуму trainer выбирает best.pt
    fitness: float | None = None


class ClassMetrics(_ResultModel):
    """Метрики одного класса операции evaluate; index — выход модели."""

    index: int
    name: str
    metrics: dict[str, float]


class DetectionBox(_ResultModel):
    """Рамка в долях анализируемого кадра (вырез, если задан region): 0..1."""

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
    """Кадр с обнаружениями. index — номер кадра видео (0 у картинки), timeMs — его время."""

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
    """Что работник увидел во входе: размер кадра, длительность и fps видео."""

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
    """Снимок монитора в outputRoot (png), проверяется как артефакт: путь, размер, sha256."""

    path: str
    size_bytes: int
    sha256: str
    width: int
    height: int
    monitor: int
    monitors: list[MonitorInfo]


class JobResult(_ResultModel):
    protocol_version: int
    # None, только если request.json не прочитан: связать результат не с чем
    operation_id: str | None
    operation: str | None
    status: ResultStatus
    started_at: str
    finished_at: str
    duration_sec: float
    error: ResultError | None = None
    warnings: list[ResultWarning] = Field(default_factory=list)
    architecture: DetectedArchitecture | None = None
    # фактически применённые параметры, включая посчитанные lr0 и разогрев
    effective_params: dict[str, Any] | None = None
    metrics: dict[str, float] | None = None
    epochs_completed: int | None = None
    stopped_early: bool | None = None
    history: list[EpochRecord] = Field(default_factory=list)
    history_file: EpochHistoryFile | None = None
    # только evaluate: метрики по классам, у которых в выборке есть объекты
    class_metrics: list[ClassMetrics] = Field(default_factory=list)
    artifacts: list[Artifact] = Field(default_factory=list)
    # только train: картинки ultralytics и консольный лог; необязательное поле протокола v1
    reports: list[ReportFile] = Field(default_factory=list)
    environment: dict[str, Any] = Field(default_factory=dict)
    # только predict/live: сводка прогона, вход и (у картинки) её единственный кадр
    summary: PredictionSummary | None = None
    source: SourceInfo | None = None
    frames: list[PredictedFrame] = Field(default_factory=list)
    # только screenshot
    screenshot: Screenshot | None = None
