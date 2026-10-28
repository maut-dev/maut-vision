"""Schema of request.json.

Fields in JSON are camelCase (this is how the host writes them), in Python snake_case.
Extra fields are forbidden: a typo in a hyperparameter name must give a refusal,
not quiet training with the default value.
"""
from __future__ import annotations

from pathlib import PurePosixPath
from typing import Annotated, Any, Literal

from pydantic import AfterValidator, BaseModel, ConfigDict, Field, TypeAdapter, ValidationError, field_validator, model_validator
from pydantic.alias_generators import to_camel

from worker.protocol.errors import ErrorCode, WorkerError
from worker.model_catalog import MODELS, SUPPORTED_ARCHITECTURES, supported_architecture

PROTOCOL_VERSION = 1

# One catalog for the UI, the backend, the worker protocol and the checkpoint check.
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
            raise ValueError(f"The input size for {architecture} must be a multiple of {MODELS[architecture]['stride']}")
        return self


def _check_relative(value: str) -> str:
    """A path inside the job folder: relative, forward slashes, without "..".

    The worker does not choose where data lies — everything it reads and writes
    is in the job folder. An absolute path or a step upward would mean that
    the request points to foreign files.
    """
    if not value or "\\" in value or ":" in value:
        raise ValueError("the path must be relative, with forward slashes")
    p = PurePosixPath(value)
    if p.is_absolute() or ".." in p.parts:
        raise ValueError("the path must stay inside the job folder")
    return p.as_posix()


RelPath = Annotated[str, Field(min_length=1, max_length=512)]


class JobInput(ProtocolModel):
    """Job inputs, relative to the job folder. Immutable during a run."""

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
            raise ValueError("inputs lie only in input/")
        return v


class Augmentations(ProtocolModel):
    """Ultralytics augmentations. The defaults are chosen for objects
    with a fixed geometry (icons, signs, logos): they
    are not flipped or tilted, and a mosaic on small frames
    turns an object into mush. Domain Datasets knows its dataset better —
    it passes the set explicitly.
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


# imgsz is a multiple of 32 (the maximum network stride): otherwise ultralytics silently
# rounds it, and training, export and result.json disagree on the size.
ImgSize = Annotated[int, Field(ge=32, multiple_of=32)]


class TrainParams(ProtocolModel):
    architecture: Architecture
    epochs: int = Field(ge=1)
    batch: int | float
    imgsz: ImgSize
    device: Device
    seed: int = Field(0, ge=0)
    # None — compute from the batch (operations/train/hyperparams.py)
    lr0: float | None = Field(None, gt=0)
    warmup_epochs: float | None = Field(None, ge=0)
    lrf: float = Field(0.01, ge=0, le=1)
    optimizer: Literal["auto", "Adam", "Adamax", "AdamW", "NAdam", "RAdam", "RMSProp", "RMSprop", "SGD", "MuSGD"] = "AdamW"
    momentum: float = Field(0.9, ge=0, le=1)
    # for Adam a high bias lr during warmup breaks the start
    warmup_bias_lr: float = Field(0.0, ge=0)
    cls: float = Field(1.0, ge=0)
    patience: int | None = Field(None, ge=0)
    # AMP is on by default, as in ultralytics. The standard AMP check
    # downloads an unrelated network, so the worker replaces it with an offline check
    # (operations/train/ultralytics_setup.py): on the CPU and on a GPU from the
    # blacklist AMP is turned off. The actual value is effectiveParams.amp.
    amp: bool | Literal["fp16", "bf16", "fp32"] = True
    # An image cache in RAM speeds up small datasets but eats memory on large
    # ones — only someone who knows the dataset size turns it on.
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
        raise ValueError("batch must be -1, a GPU fraction (0,1) or a positive integer")

    @field_validator("scale")
    @classmethod
    def _valid_scale(cls, value: float | tuple[float, float]):
        if isinstance(value, tuple):
            if value[0] <= 0 or value[1] < value[0]:
                raise ValueError("scale requires a positive ordered range")
        elif value < 0:
            raise ValueError("scale must be non-negative")
        return value

    @field_validator("fraction")
    @classmethod
    def _valid_fraction(cls, value):
        values = value if isinstance(value, list) else [value]
        if len(values) == 0 or len(values) > 3:
            raise ValueError("fraction must contain one to three splits")
        for index, item in enumerate(values):
            if item == 0 and index == 2:
                continue
            if not (0 < item <= 1 or item > 1 and float(item).is_integer()):
                raise ValueError("fraction requires a share (0,1] or an integer number of images")
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
            raise ValueError("freeze requires layer indexes or module names like 23.cv2")
        return value

    @field_validator("classes")
    @classmethod
    def _valid_classes(cls, value):
        if value is not None and (not value or len(set(value)) != len(value) or any(item < 0 for item in value)):
            raise ValueError("classes requires a non-empty list of unique indexes")
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
            raise ValueError("inputs lie only in input/")
        return v


class PredictInput(ProtocolModel):
    """Version weights and a source file (an image or a video).

    The source is not copied: the host puts a hard link into input/ (source), and if
    the file is on another volume and a link is impossible, it passes its absolute path
    (externalSource). Exactly one of the two. The external file is only read.
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
            raise ValueError("inputs lie only in input/")
        return v

    @field_validator("external_source")
    @classmethod
    def _absolute(cls, v: str | None) -> str | None:
        if v is None:
            return v
        from pathlib import PureWindowsPath

        if not PureWindowsPath(v).is_absolute() or ".." in PureWindowsPath(v).parts:
            raise ValueError("externalSource must be an absolute path without '..'")
        return v

    @model_validator(mode="after")
    def _one_source(self):
        if (self.source is None) == (self.external_source is None):
            raise ValueError("exactly one source is needed: source or externalSource")
        return self


class LiveInput(_InputBase):
    """Version weights; the source is the screen, no files are needed."""

    model: RelPath
    manifest: RelPath | None = "input/manifest.json"


class Region(ProtocolModel):
    """A rectangle in pixels of the source frame (for the screen — relative to the monitor)."""

    x: int = Field(ge=0)
    y: int = Field(ge=0)
    width: int = Field(ge=1)
    height: int = Field(ge=1)


# Ultralytics confidence threshold: below 0.01 is a stream of garbage, 1.0 is nothing.
Confidence = Annotated[float, Field(ge=0, le=1.0)]


class PredictParams(ProtocolModel):
    """A run of the weights on an image or a video (model.predict).

    A video is read sequentially (grab without decoding the skipped frames),
    not by seeking: seek in OpenCV is an order of magnitude slower (measured).
    """

    architecture: Architecture
    imgsz: ImgSize
    device: Device
    conf: Confidence = 0.25
    source: Literal["image", "video"]
    # video analysis step; not used for an image
    step_ms: int = Field(1000, ge=1)
    # None — the whole frame; otherwise a cut-out in pixels of the source frame
    region: Region | None = None
    max_detections: int | None = Field(None, ge=1)
    # preview frame (jpeg in the frame event) no more often than this interval
    preview_interval_ms: int = Field(1000, ge=0, le=600_000)


class MonitorBounds(ProtocolModel):
    """Monitor bounds in physical pixels as the host saw them when choosing the crop."""

    left: int
    top: int
    width: int = Field(ge=1)
    height: int = Field(ge=1)


class LiveParams(ProtocolModel):
    """The loop "screen capture → predict → event" until cancellation or maxDurationSec.

    expectedMonitor — the monitor bounds on the screenshot on which the crop was chosen: if
    the capture sees the monitor differently (monitors or scaling changed), the crop would point
    elsewhere, so this is a refusal, not a silent shift.
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
            raise ValueError("the output cannot lie in the inputs")
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
    """A screenshot for choosing the crop: there are no inputs, no weights are needed."""

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
    """Validate the raw request JSON. A refusal is a WorkerError with a clear code.

    The protocol version is checked before the common schema: a request of a new version almost
    certainly fails the rest of the validation too, and then a person would see
    a dozen "extra fields" instead of the single real reason.
    """
    if not isinstance(raw, dict):
        raise WorkerError(ErrorCode.REQUEST_INVALID, "not_object")
    version = raw.get("protocolVersion")
    if version != PROTOCOL_VERSION:
        raise WorkerError(
            ErrorCode.PROTOCOL_UNSUPPORTED,
            args={"received": repr(version), "supported": PROTOCOL_VERSION},
            details={"received": version, "supported": [PROTOCOL_VERSION]},
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
            "validation",
            {"summary": summary},
            {"problems": problems},
        ) from None
