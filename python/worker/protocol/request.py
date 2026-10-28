"""Схема request.json.

Поля в JSON — camelCase (так их пишет host), в Python — snake_case.
Лишние поля запрещены: опечатка в имени гиперпараметра должна давать отказ,
а не тихое обучение со значением по умолчанию.
"""
from __future__ import annotations

from pathlib import PurePosixPath
from typing import Annotated, Any, Literal

from pydantic import AfterValidator, BaseModel, ConfigDict, Field, TypeAdapter, ValidationError, field_validator, model_validator
from pydantic.alias_generators import to_camel

from worker.protocol.errors import ErrorCode, WorkerError
from worker.model_catalog import MODELS, SUPPORTED_ARCHITECTURES, supported_architecture

PROTOCOL_VERSION = 1

# Один каталог для UI, backend, протокола работника и проверки чекпоинта.
Architecture = Annotated[str, AfterValidator(supported_architecture)]


class ProtocolModel(BaseModel):
    model_config = ConfigDict(
        alias_generator=to_camel,
        populate_by_name=True,
        extra="forbid",
        frozen=True,
        allow_inf_nan=False,
    )

    @model_validator(mode="after")
    def model_image_stride(self):
        architecture = getattr(self, "architecture", None)
        imgsz = getattr(self, "imgsz", None)
        if architecture in MODELS and imgsz is not None and imgsz % MODELS[architecture]["stride"]:
            raise ValueError(f"Размер входа для {architecture} должен быть кратен {MODELS[architecture]['stride']}")
        return self


def _check_relative(value: str) -> str:
    """Путь внутри папки задачи: относительный, прямые слэши, без «..».

    Работник не выбирает расположение данных — всё, что он читает и пишет,
    лежит в папке задачи. Абсолютный путь или выход наверх означал бы, что
    запрос указывает в чужие файлы.
    """
    if not value or "\\" in value or ":" in value:
        raise ValueError("путь должен быть относительным, с прямыми слэшами")
    p = PurePosixPath(value)
    if p.is_absolute() or ".." in p.parts:
        raise ValueError("путь должен оставаться внутри папки задачи")
    return p.as_posix()


RelPath = Annotated[str, Field(min_length=1, max_length=512)]


class JobInput(ProtocolModel):
    """Входы задачи, относительно папки задачи. Неизменяемы во время запуска."""

    dataset: RelPath = "input/dataset"
    model: RelPath
    manifest: RelPath | None = "input/manifest.json"

    @field_validator("dataset", "model", "manifest")
    @classmethod
    def _inside_input(cls, v: str | None) -> str | None:
        if v is None:
            return v
        v = _check_relative(v)
        if not v.startswith("input/"):
            raise ValueError("входы лежат только в input/")
        return v


class Augmentations(ProtocolModel):
    """Аугментации ultralytics. Значения по умолчанию подобраны для объектов
    с фиксированной геометрией (значки, знаки, логотипы): они
    не переворачиваются и не наклоняются, а мозаика на маленьких кадрах
    превращает объект в кашу. Domain Datasets знает свой датасет лучше —
    он передаёт набор явно.
    """

    fliplr: float = Field(0.0, ge=0, le=1)
    flipud: float = Field(0.0, ge=0, le=1)
    degrees: float = Field(0.0, ge=0, le=180)
    shear: float = Field(0.0, ge=0, le=90)
    perspective: float = Field(0.0, ge=0, le=0.001)
    mosaic: float = Field(0.0, ge=0, le=1)
    close_mosaic: int = Field(10, ge=0)
    mixup: float = Field(0.0, ge=0, le=1)
    copy_paste: float = Field(0.0, ge=0, le=1)
    scale: float = Field(0.10, ge=0, le=0.9)
    translate: float = Field(0.05, ge=0, le=0.9)
    hsv_h: float = Field(0.01, ge=0, le=1)
    hsv_s: float = Field(0.4, ge=0, le=1)
    hsv_v: float = Field(0.5, ge=0, le=1)


Device = Annotated[str, Field(pattern=r"^(cpu|-1|[0-9]+)$")]


# imgsz кратен 32 (максимальный шаг сети): иначе ultralytics молча
# округлит его, и обучение, экспорт и result.json разойдутся в размере.
ImgSize = Annotated[int, Field(ge=32, multiple_of=32)]


class TrainParams(ProtocolModel):
    architecture: Architecture
    epochs: int = Field(ge=1)
    batch: int | float
    imgsz: ImgSize
    device: Device
    seed: int = Field(0, ge=0)
    # None — посчитать по батчу (operations/train/hyperparams.py)
    lr0: float | None = Field(None, gt=0)
    warmup_epochs: float | None = Field(None, ge=0)
    lrf: float = Field(0.01, ge=0, le=1)
    optimizer: Literal["auto", "Adam", "Adamax", "AdamW", "NAdam", "RAdam", "RMSProp", "RMSprop", "SGD", "MuSGD"] = "AdamW"
    momentum: float = Field(0.9, ge=0, le=1)
    # для Adam высокий lr смещений на разогреве ломает старт
    warmup_bias_lr: float = Field(0.0, ge=0)
    cls: float = Field(1.0, ge=0)
    patience: int | None = Field(None, ge=0)
    # AMP по умолчанию включён, как у ultralytics. Штатная проверка AMP
    # скачивает постороннюю сеть, поэтому работник подменяет её офлайн-проверкой
    # (operations/train/ultralytics_setup.py): на CPU и на GPU из чёрного
    # списка AMP отключается. Фактическое значение — effectiveParams.amp.
    amp: bool | Literal["fp16", "bf16", "fp32"] = True
    # Кэш картинок в RAM ускоряет маленькие датасеты, но на больших съедает
    # память — включает только тот, кто знает размер датасета.
    cache: bool | Literal["ram", "disk"] = False
    augmentations: Augmentations = Augmentations()
    export_onnx: bool = True
    time: float | None = Field(None, gt=0)
    workers: int | None = Field(None, ge=0)
    weight_decay: float = Field(0.0005, ge=0)
    warmup_momentum: float = Field(0.8, ge=0, le=1)
    cos_lr: bool = False
    nbs: int = Field(64, ge=1)
    box: float = Field(7.5, ge=0)
    dfl: float = Field(1.5, ge=0)
    cls_pw: float = Field(0.0, ge=0)
    cls_remap: bool = True
    rect: bool = False
    multi_scale: float = Field(0.0, ge=0)
    fraction: int | float | list[int | float] = 1.0
    freeze: int | list[int | str] | None = None
    deterministic: bool = True
    pretrained: bool = True
    single_cls: bool = False
    classes: list[int] | None = None
    save: bool = True
    save_period: int = Field(-1, ge=-1)
    val: bool = True
    plots: bool = True
    verbose: bool = True
    profile: bool = False
    compile: bool | Literal["default", "reduce-overhead", "max-autotune-no-cudagraphs"] = False
    channels_last: bool | None = None
    conf: float | None = Field(None, ge=0, le=1)
    iou: float = Field(0.7, ge=0, le=1)
    max_det: int = Field(300, ge=1)
    save_json: bool = False
    split: Literal["train", "val", "test"] = "val"
    # Flat fields are authoritative for new requests. Legacy nested requests remain readable.
    fliplr: float = Field(0.0, ge=0, le=1)
    flipud: float = Field(0.0, ge=0, le=1)
    degrees: float = Field(0.0, ge=0)
    shear: float = Field(0.0, ge=0)
    perspective: float = Field(0.0, ge=0, le=1)
    mosaic: float = Field(0.0, ge=0, le=1)
    close_mosaic: int = Field(10, ge=0)
    mixup: float = Field(0.0, ge=0, le=1)
    cutmix: float = Field(0.0, ge=0, le=1)
    copy_paste: float = Field(0.0, ge=0, le=1)
    scale: float | tuple[float, float] = 0.1
    translate: float = Field(0.05, ge=0, le=1)
    hsv_h: float = Field(0.01, ge=0, le=1)
    hsv_s: float = Field(0.4, ge=0, le=1)
    hsv_v: float = Field(0.5, ge=0, le=1)
    bgr: float = Field(0.0, ge=0, le=1)

    @field_validator("batch")
    @classmethod
    def _valid_batch(cls, value: int | float) -> int | float:
        if value == -1 or 0 < value < 1 or value >= 1 and float(value).is_integer():
            return value
        raise ValueError("batch должен быть -1, долей GPU (0,1) или положительным целым")

    @field_validator("scale")
    @classmethod
    def _valid_scale(cls, value: float | tuple[float, float]):
        if isinstance(value, tuple):
            if value[0] <= 0 or value[1] < value[0]:
                raise ValueError("scale требует положительный упорядоченный диапазон")
        elif value < 0:
            raise ValueError("scale должен быть неотрицательным")
        return value

    @field_validator("fraction")
    @classmethod
    def _valid_fraction(cls, value):
        values = value if isinstance(value, list) else [value]
        if len(values) == 0 or len(values) > 3:
            raise ValueError("fraction должен содержать от одной до трёх выборок")
        for index, item in enumerate(values):
            if item == 0 and index == 2:
                continue
            if not (0 < item <= 1 or item > 1 and float(item).is_integer()):
                raise ValueError("fraction требует долю (0,1] или целое число изображений")
        return value

    @field_validator("freeze")
    @classmethod
    def _valid_freeze(cls, value):
        import re
        items = value if isinstance(value, list) else [] if value is None else [value]
        for item in items:
            if isinstance(item, int) and item >= 0:
                continue
            if isinstance(item, str) and re.fullmatch(r"[0-9]+(?:\.[A-Za-z0-9_]+)*", item):
                continue
            raise ValueError("freeze требует индексы слоёв или имена модулей вида 23.cv2")
        return value

    @field_validator("classes")
    @classmethod
    def _valid_classes(cls, value):
        if value is not None and (not value or len(set(value)) != len(value) or any(item < 0 for item in value)):
            raise ValueError("classes требует непустой список уникальных индексов")
        return value


class EvaluateParams(ProtocolModel):
    architecture: Architecture
    imgsz: ImgSize
    batch: int = Field(16, ge=1)
    device: Device
    split: Literal["val", "test"] = "val"


class ExportParams(ProtocolModel):
    architecture: Architecture
    imgsz: ImgSize
    format: Literal["onnx"] = "onnx"
    dynamic: bool = True
    simplify: bool = True


class _InputBase(ProtocolModel):
    @field_validator("*")
    @classmethod
    def _inside_input(cls, v: str | None) -> str | None:
        if v is None:
            return v
        v = _check_relative(v)
        if not v.startswith("input/"):
            raise ValueError("входы лежат только в input/")
        return v


class PredictInput(ProtocolModel):
    """Веса версии и файл-источник (картинка или видео).

    Источник не копируется: host кладёт в input/ жёсткую ссылку (source), а если
    файл на другом томе и ссылка невозможна — передаёт его абсолютный путь
    (externalSource). Ровно одно из двух. Внешний файл только читается.
    """

    model: RelPath
    source: RelPath | None = None
    external_source: Annotated[str, Field(min_length=3, max_length=1024)] | None = None
    manifest: RelPath | None = "input/manifest.json"

    @field_validator("model", "source", "manifest")
    @classmethod
    def _inside_input(cls, v: str | None) -> str | None:
        if v is None:
            return v
        v = _check_relative(v)
        if not v.startswith("input/"):
            raise ValueError("входы лежат только в input/")
        return v

    @field_validator("external_source")
    @classmethod
    def _absolute(cls, v: str | None) -> str | None:
        if v is None:
            return v
        from pathlib import PureWindowsPath

        if not PureWindowsPath(v).is_absolute() or ".." in PureWindowsPath(v).parts:
            raise ValueError("externalSource должен быть абсолютным путём без «..»")
        return v

    @model_validator(mode="after")
    def _one_source(self):
        if (self.source is None) == (self.external_source is None):
            raise ValueError("нужен ровно один источник: source или externalSource")
        return self


class LiveInput(_InputBase):
    """Веса версии; источник — экран, файлов не нужно."""

    model: RelPath
    manifest: RelPath | None = "input/manifest.json"


class Region(ProtocolModel):
    """Прямоугольник в пикселях исходного кадра (для экрана — относительно монитора)."""

    x: int = Field(ge=0)
    y: int = Field(ge=0)
    width: int = Field(ge=1)
    height: int = Field(ge=1)


# Порог уверенности ultralytics: ниже 0.01 — поток мусора, 1.0 — ничего.
Confidence = Annotated[float, Field(ge=0, le=1.0)]


class PredictParams(ProtocolModel):
    """Прогон весов по картинке или видео (model.predict).

    Видео читается последовательно (grab без декодирования пропускаемых кадров),
    а не перемоткой: seek в OpenCV на порядок медленнее (по замеру).
    """

    architecture: Architecture
    imgsz: ImgSize
    device: Device
    conf: Confidence = 0.25
    source: Literal["image", "video"]
    # шаг анализа видео; у картинки не используется
    step_ms: int = Field(1000, ge=1)
    # None — весь кадр; иначе вырез в пикселях исходного кадра
    region: Region | None = None
    max_detections: int | None = Field(None, ge=1)
    # кадр-превью (jpeg в событии frame) не чаще этого интервала
    preview_interval_ms: int = Field(1000, ge=0, le=600_000)


class MonitorBounds(ProtocolModel):
    """Границы монитора в физических пикселях, какими их видел host при выборе рамки."""

    left: int
    top: int
    width: int = Field(ge=1)
    height: int = Field(ge=1)


class LiveParams(ProtocolModel):
    """Цикл «захват экрана → predict → событие» до отмены или maxDurationSec.

    expectedMonitor — границы монитора на снимке, по которому выбрана рамка: если
    захват видит монитор иначе (сменились мониторы или масштаб), рамка указала бы
    в другое место, поэтому это отказ, а не молчаливое смещение.
    """

    architecture: Architecture
    imgsz: ImgSize
    device: Device
    conf: Confidence = 0.25
    monitor: int = Field(1, ge=0 )
    region: Region | None = None
    fps: float = Field(3.0, gt=0)
    max_duration_sec: int = Field(1800, ge=1)
    max_detections: int | None = Field(None, ge=1)
    preview_interval_ms: int = Field(1000, ge=0, le=600_000)
    expected_monitor: MonitorBounds | None = None


class ScreenshotParams(ProtocolModel):
    monitor: int = Field(1, ge=0 )


class _RequestBase(ProtocolModel):
    protocol_version: Literal[1]
    operation_id: str = Field(min_length=1, max_length=128, pattern=r"^[A-Za-z0-9._-]+$")
    output_root: RelPath = "output"

    @field_validator("output_root")
    @classmethod
    def _output_inside(cls, v: str) -> str:
        v = _check_relative(v)
        if v == "input" or v.startswith("input/"):
            raise ValueError("вывод не может лежать во входах")
        return v


class TrainRequest(_RequestBase):
    operation: Literal["train"]
    input: JobInput
    params: TrainParams


class EvaluateRequest(_RequestBase):
    operation: Literal["evaluate"]
    input: JobInput
    params: EvaluateParams


class ExportRequest(_RequestBase):
    operation: Literal["export"]
    input: JobInput
    params: ExportParams


class PredictRequest(_RequestBase):
    operation: Literal["predict"]
    input: PredictInput
    params: PredictParams


class LiveRequest(_RequestBase):
    operation: Literal["live"]
    input: LiveInput
    params: LiveParams


class ScreenshotRequest(_RequestBase):
    """Снимок экрана для выбора рамки: входов нет, весов не нужно."""

    operation: Literal["screenshot"]
    params: ScreenshotParams = ScreenshotParams()


AnyRequest = TrainRequest | EvaluateRequest | ExportRequest | PredictRequest | LiveRequest | ScreenshotRequest

JobRequest = Annotated[
    AnyRequest,
    Field(discriminator="operation"),
]
Operation = Literal["train", "evaluate", "export", "predict", "live", "screenshot"]

_request_adapter: TypeAdapter[JobRequest] = TypeAdapter(JobRequest)


def parse_request(raw: Any) -> AnyRequest:
    """Проверить сырой JSON запроса. Отказ — WorkerError с понятным кодом.

    Версию протокола смотрим до общей схемы: запрос новой версии почти
    наверняка не пройдёт и остальную валидацию, и тогда человек увидит
    десяток «лишних полей» вместо единственной настоящей причины.
    """
    if not isinstance(raw, dict):
        raise WorkerError(ErrorCode.REQUEST_INVALID, "request.json должен быть JSON-объектом")
    version = raw.get("protocolVersion")
    if version != PROTOCOL_VERSION:
        raise WorkerError(
            ErrorCode.PROTOCOL_UNSUPPORTED,
            f"Версия протокола {version!r} не поддерживается, работник понимает {PROTOCOL_VERSION}",
            {"received": version, "supported": [PROTOCOL_VERSION]},
        )
    try:
        return _request_adapter.validate_python(raw)
    except ValidationError as exc:
        problems = [
            {
                "path": ".".join(str(p) for p in e["loc"]),
                "message": e["msg"],
            }
            for e in exc.errors(include_url=False)
        ]
        summary = "; ".join(f"{p['path']}: {p['message']}" for p in problems[:5])
        raise WorkerError(
            ErrorCode.REQUEST_INVALID,
            f"Запрос не прошёл проверку: {summary}",
            {"problems": problems},
        ) from None
