"""Training: input checks → training → artifacts.

The order matters: everything that can be checked without training (dataset, weights file,
architecture, device) is checked before it — so that a refusal arrives within
seconds, not after an hour on the GPU.
"""
from __future__ import annotations

import shutil
import sys
import traceback
from pathlib import Path
from typing import Any

from worker.operations.context import OperationContext
from worker.operations.train import architecture, dataset, hyperparams, reports, ultralytics_setup
from worker.operations.train.options import resolved_options, trainer_snapshot
from worker.operations.train.progress import TrainingProgress, map_metrics
from worker.protocol import (
    Artifact,
    ArtifactKind,
    ErrorCode,
    ReportFile,
    ReportKind,
    TrainParams,
    TrainRequest,
    WorkerError,
)
from worker.runtime.job_layout import sha256_file
from worker.runtime.streams import capture_console, redact_console_paths

RUN_NAME = "train"
# raw console output of training (draft) and its cleaned report copy
CONSOLE_RAW = "console.raw.log"
CONSOLE_LOG = "training.log"


def _load_model(path: Path) -> Any:
    """Load the base weights only from a file.

    No names like "yolo12n.pt" without a path: ultralytics, not finding such
    a file, would download it, and a .yaml would build a network with random weights.
    So an existing .pt is required.
    """
    if path.suffix.lower() != ".pt":
        raise WorkerError(ErrorCode.MODEL_LOAD_FAILED, "not_pt", {"file": path.name})
    if not path.is_file():
        raise WorkerError(ErrorCode.MODEL_NOT_FOUND, args={"file": path.name})
    from ultralytics import YOLO

    try:
        return YOLO(str(path))
    except Exception as exc:  # noqa: BLE001 — the checkpoint is broken or of a foreign format: the reason goes to the result
        traceback.print_exc(file=sys.stderr)
        raise WorkerError(
            ErrorCode.MODEL_LOAD_FAILED,
            "load_error",
            {"file": path.name, "error": f"{type(exc).__name__}: {exc}"[:1000]},
        ) from None


def _check_device(device: str) -> str | int:
    import torch

    if device == "cpu":
        return "cpu"
    index = int(device)
    if not torch.cuda.is_available():
        raise WorkerError(ErrorCode.DEVICE_UNAVAILABLE, "no_cuda", {"index": index})
    if index >= torch.cuda.device_count():
        raise WorkerError(ErrorCode.DEVICE_UNAVAILABLE, "gpu_missing", {"index": index, "count": torch.cuda.device_count()})
    return index


def _effective_params(p: TrainParams, workers: int) -> dict[str, Any]:
    return resolved_options(p, workers)[1]


def _artifact(ctx: OperationContext, kind: ArtifactKind, path: Path) -> Artifact:
    return Artifact(
        kind=kind,
        path=ctx.layout.relative_to_output(path),
        size_bytes=path.stat().st_size,
        sha256=sha256_file(path),
    )


def _report(ctx: OperationContext, kind: ReportKind, path: Path) -> ReportFile:
    return ReportFile(
        kind=kind,
        path=ctx.layout.relative_to_output(path),
        size_bytes=path.stat().st_size,
        sha256=sha256_file(path),
    )


def _collect_reports(ctx: OperationContext, run_dir: Path | None) -> list[ReportFile]:
    """Ultralytics images and the console log → output/artifacts/{plots/, training.log}.

    A collection failure is a warning: the training has already happened, the passport will simply
    have no images or log.
    """
    found: list[ReportFile] = []
    try:
        if run_dir is not None:
            for plot in reports.collect_plots(run_dir, ctx.layout.artifacts_dir / "plots"):
                found.append(_report(ctx, ReportKind.PLOT, plot))
        log = reports.write_console_log(ctx.layout.work_dir / CONSOLE_RAW, ctx.layout.artifacts_dir / CONSOLE_LOG)
        if log is not None:
            found.append(_report(ctx, ReportKind.LOG, log))
    except Exception as exc:  # noqa: BLE001 — see the docstring
        traceback.print_exc(file=sys.stderr)
        ctx.warn("reports_failed", args={"error": f"{type(exc).__name__}: {exc}"[:500]})
        return []
    return found


def _export_onnx(ctx: OperationContext, best: Path, imgsz: int) -> Path | None:
    """ONNX from best.pt. An export failure is a warning, not a job failure:
    the training has already happened and best.pt is intact."""
    from ultralytics import YOLO

    try:
        # export on the CPU: it does not share GPU memory with the training that just
        # finished, and the result does not depend on the device
        out = YOLO(str(best)).export(format="onnx", imgsz=imgsz, dynamic=True, simplify=True, device="cpu")
    except Exception as exc:  # noqa: BLE001 — see the docstring
        traceback.print_exc(file=sys.stderr)
        ctx.warn("onnx_export_failed", "export_error", {"error": f"{type(exc).__name__}: {exc}"[:500]})
        return None
    onnx = Path(str(out))
    if not onnx.is_file():
        ctx.warn("onnx_export_failed", "no_file")
        return None
    target = ctx.layout.artifacts_dir / "best.onnx"
    if onnx.resolve() != target.resolve():
        shutil.move(str(onnx), target)
    return target


def run_train(request: TrainRequest, ctx: OperationContext) -> dict[str, Any]:
    p = request.params
    layout = ctx.layout

    ctx.stage("validating")
    data_yaml, dataset_summary = dataset.prepare(layout.resolve(request.input.dataset), layout.work_dir)
    dataset.select_training_validation_split(data_yaml, p.split, dataset_summary)
    workers = p.workers if p.workers is not None else hyperparams.dataloader_workers()
    options, effective = resolved_options(p, workers)
    effective["dataset"] = dataset_summary
    effective["validationSplit"] = p.split
    if p.classes is not None and any(index >= dataset_summary["classes"] for index in p.classes):
        raise WorkerError(ErrorCode.REQUEST_INVALID, "class_out_of_range")
    if p.compile:
        raise WorkerError(ErrorCode.REQUEST_INVALID, "compile_unavailable")
    if p.profile:
        raise WorkerError(ErrorCode.REQUEST_INVALID, "profile_unavailable")
    ctx.events.emit("effectiveParams", parameters=effective, capture="requested")

    ultralytics_setup.configure(layout.work_dir)
    device = _check_device(p.device)
    # The environment goes before training: the host keeps it even for an interrupted or crashed run.
    ctx.events.emit("environment", environment=_environment(str(device)))

    ctx.stage("loading_model")
    model = _load_model(layout.resolve(request.input.model))
    detected = architecture.verify(p.architecture, model, "base_weights")
    # Keep the verified label in new checkpoints, including legacy YOLOv8.
    # When a trained best.pt is moved, the source weights path may already be unavailable.
    if not model.model.yaml.get("yaml_file"):
        model.model.yaml["yaml_file"] = detected.yaml_file

    ctx.stage("training")
    progress = TrainingProgress(ctx.events, p.epochs, layout.output_dir / "history.jsonl")
    progress.attach(model)
    def publish_effective(trainer: Any) -> None:
        nonlocal effective
        snapshot = trainer_snapshot(trainer, effective)
        if snapshot != effective:
            effective = snapshot
            ctx.events.emit("effectiveParams", parameters=effective, capture="trainer")
    model.add_callback("on_train_start", publish_effective)
    model.add_callback("on_train_epoch_start", publish_effective)
    run_dir = layout.work_dir / "run"
    try:
        # The training console output goes to a separate file for the version passport
        # (it does not touch the stdout protocol: all of it goes to stderr anyway).
        with capture_console(layout.work_dir / CONSOLE_RAW, on_line=lambda message: ctx.events.emit("log", message=redact_console_paths(message, layout.job_dir))):
            model.train(
                data=str(data_yaml),
                device=device,
                project=str(run_dir),
                name=RUN_NAME,
                exist_ok=True,
                # Ultralytics images (results.png, confusion matrices, batches,
                # curves) are report files of the version passport. The font for labels
                # is looked up only locally (ultralytics_setup), without the network.
                **{key: value for key, value in options.items() if key != "device"},
            )
    except WorkerError:
        raise
    except Exception as exc:  # noqa: BLE001 — any training failure is described in result.json
        traceback.print_exc(file=sys.stderr)
        oom = "out of memory" in str(exc).lower()
        raise WorkerError(
            ErrorCode.OUT_OF_MEMORY if oom else ErrorCode.TRAINING_FAILED,
            "train" if oom else None,
            {"error": f"{type(exc).__name__}: {exc}"[:1000]},
            {"epochsCompleted": progress.count},
        ) from None

    trainer = model.trainer
    publish_effective(trainer)
    # Ultralytics reduces the batch itself on the first epoch when memory is short —
    # then lr0, computed for the original batch, no longer matches it.
    actual_batch = int(getattr(trainer, "batch_size", p.batch) or p.batch)
    effective["effectiveBatch"] = actual_batch
    if actual_batch != p.batch and p.batch >= 1:
        ctx.warn("batch_reduced", args={"requested": p.batch, "actual": actual_batch, "lr0": effective["lr0"]})

    # AMP was requested but the trainer disabled it: CPU or a GPU from the blacklist
    # (ultralytics_setup._offline_check_amp). The passport gets the actual value.
    actual_amp = bool(getattr(trainer, "amp", p.amp))
    effective["amp"] = actual_amp
    if p.amp not in (False, "fp32") and not actual_amp and p.device != "cpu":
        ctx.warn("amp_disabled")

    ctx.stage("finalizing")
    best = Path(trainer.best)
    if not best.is_file() and Path(trainer.last).is_file():
        best = Path(trainer.last)
        effective["checkpointSelection"] = "last"
    else:
        effective["checkpointSelection"] = "best"
    if not best.is_file():
        raise WorkerError(ErrorCode.NO_WEIGHTS_PRODUCED)
    layout.artifacts_dir.mkdir(parents=True, exist_ok=True)
    best_artifact = layout.artifacts_dir / "best.pt"
    shutil.copyfile(best, best_artifact)
    from ultralytics import YOLO

    architecture.verify(p.architecture, YOLO(str(best_artifact)), "best_pt")

    artifacts = [_artifact(ctx, ArtifactKind.WEIGHTS_PT, best_artifact)]
    if p.export_onnx:
        ctx.stage("exporting")
        onnx = _export_onnx(ctx, best_artifact, p.imgsz)
        if onnx is not None:
            artifacts.append(_artifact(ctx, ArtifactKind.ONNX, onnx))

    save_dir = getattr(trainer, "save_dir", None)
    report_files = _collect_reports(ctx, Path(save_dir) if save_dir else None)

    metrics = progress.final_metrics or map_metrics(getattr(trainer, "metrics", None) or {})
    return {
        "architecture": detected,
        "effective_params": effective,
        "metrics": metrics,
        "epochs_completed": progress.count,
        "stopped_early": progress.count < p.epochs,
        "history_file": {"path": "history.jsonl", "size_bytes": progress.history_path.stat().st_size, "sha256": sha256_file(progress.history_path), "count": progress.count},
        "artifacts": artifacts,
        "reports": report_files,
        "environment": _environment(str(trainer.device).replace("cuda:", "")),
    }


def _environment(device: str | int) -> dict[str, Any]:
    import platform

    import torch
    import ultralytics

    env: dict[str, Any] = {
        "python": platform.python_version(),
        "torch": torch.__version__,
        "ultralytics": ultralytics.__version__,
        "cuda": torch.version.cuda,
        "cudaAvailable": torch.cuda.is_available(),
    }
    # -1 — automatic choice: the GPU name becomes known when the trainer picks it (final environment).
    if device not in ("cpu", "-1") and torch.cuda.is_available():
        env["deviceName"] = torch.cuda.get_device_name(int(device))
    return env
