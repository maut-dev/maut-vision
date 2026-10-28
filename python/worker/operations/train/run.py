"""Обучение: проверка входов → обучение → артефакты.

Порядок важен: всё, что можно проверить без обучения (датасет, файл весов,
архитектура, устройство), проверяется до него — чтобы отказ приходил за
секунды, а не после часа на GPU.
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
# сырой консольный вывод обучения (черновик) и его очищенная копия-отчёт
CONSOLE_RAW = "console.raw.log"
CONSOLE_LOG = "training.log"


def _load_model(path: Path) -> Any:
    """Загрузить базовые веса только из файла.

    Никаких имён вроде «yolo12n.pt» без пути: ultralytics, не найдя такой
    файл, скачал бы его, а .yaml построил бы сеть со случайными весами.
    Поэтому требуем существующий .pt.
    """
    if path.suffix.lower() != ".pt":
        raise WorkerError(ErrorCode.MODEL_LOAD_FAILED, f"Базовые веса должны быть файлом .pt, получено '{path.name}'")
    if not path.is_file():
        raise WorkerError(ErrorCode.MODEL_NOT_FOUND, f"Нет файла базовых весов '{path.name}' в input/model")
    from ultralytics import YOLO

    try:
        return YOLO(str(path))
    except Exception as exc:  # noqa: BLE001 — чекпоинт битый или чужого формата: причина уходит в result
        traceback.print_exc(file=sys.stderr)
        raise WorkerError(
            ErrorCode.MODEL_LOAD_FAILED,
            f"Не удалось загрузить веса '{path.name}': {type(exc).__name__}: {exc}"[:1000],
        ) from None


def _check_device(device: str) -> str | int:
    import torch

    if device == "cpu":
        return "cpu"
    index = int(device)
    if not torch.cuda.is_available():
        raise WorkerError(ErrorCode.DEVICE_UNAVAILABLE, f"Запрошен GPU {index}, но CUDA недоступна")
    if index >= torch.cuda.device_count():
        raise WorkerError(
            ErrorCode.DEVICE_UNAVAILABLE,
            f"Запрошен GPU {index}, а видно {torch.cuda.device_count()}",
        )
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
    """Картинки ultralytics и консольный лог → output/artifacts/{plots/, training.log}.

    Сбой сбора — предупреждение: обучение уже состоялось, паспорт просто
    будет без картинок или лога.
    """
    found: list[ReportFile] = []
    try:
        if run_dir is not None:
            for plot in reports.collect_plots(run_dir, ctx.layout.artifacts_dir / "plots"):
                found.append(_report(ctx, ReportKind.PLOT, plot))
        log = reports.write_console_log(ctx.layout.work_dir / CONSOLE_RAW, ctx.layout.artifacts_dir / CONSOLE_LOG)
        if log is not None:
            found.append(_report(ctx, ReportKind.LOG, log))
    except Exception as exc:  # noqa: BLE001 — см. докстринг
        traceback.print_exc(file=sys.stderr)
        ctx.warn("reports_failed", f"Картинки и лог обучения не сохранены: {type(exc).__name__}: {exc}"[:500])
        return []
    return found


def _export_onnx(ctx: OperationContext, best: Path, imgsz: int) -> Path | None:
    """ONNX из best.pt. Сбой экспорта — предупреждение, а не провал задачи:
    обучение уже состоялось и best.pt цел."""
    from ultralytics import YOLO

    try:
        # экспорт на CPU: не делит видеопамять с только что закончившимся
        # обучением, а результат от устройства не зависит
        out = YOLO(str(best)).export(format="onnx", imgsz=imgsz, dynamic=True, simplify=True, device="cpu")
    except Exception as exc:  # noqa: BLE001 — см. докстринг
        traceback.print_exc(file=sys.stderr)
        ctx.warn("onnx_export_failed", f"ONNX не экспортирован: {type(exc).__name__}: {exc}"[:500])
        return None
    onnx = Path(str(out))
    if not onnx.is_file():
        ctx.warn("onnx_export_failed", "Экспорт ONNX завершился без файла")
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
        raise WorkerError(ErrorCode.REQUEST_INVALID, "Фильтр classes содержит индекс вне карты датасета")
    if p.compile:
        raise WorkerError(ErrorCode.REQUEST_INVALID, "torch.compile недоступен в закреплённой Windows-сборке без Triton")
    if p.profile:
        raise WorkerError(ErrorCode.REQUEST_INVALID, "Профилирование требует TensorRT/ONNXRuntime и отключённых внешних интеграций")
    ctx.events.emit("effectiveParams", parameters=effective, capture="requested")

    ultralytics_setup.configure(layout.work_dir)
    device = _check_device(p.device)
    # Окружение — до обучения: host сохраняет его и у прерванного или упавшего запуска.
    ctx.events.emit("environment", environment=_environment(str(device)))

    ctx.stage("loading_model")
    model = _load_model(layout.resolve(request.input.model))
    detected = architecture.verify(p.architecture, model, "исходные веса")
    # Сохраняем проверенную метку в новых чекпоинтах, включая legacy YOLOv8.
    # При переносе trained best.pt исходный путь весов уже может быть недоступен.
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
        # Консольный вывод обучения — отдельным файлом для паспорта версии
        # (stdout-протокол он не трогает: всё это и так уходит в stderr).
        with capture_console(layout.work_dir / CONSOLE_RAW, on_line=lambda message: ctx.events.emit("log", message=redact_console_paths(message, layout.job_dir))):
            model.train(
                data=str(data_yaml),
                device=device,
                project=str(run_dir),
                name=RUN_NAME,
                exist_ok=True,
                # Картинки ultralytics (results.png, матрицы ошибок, батчи,
                # кривые) — отчётные файлы паспорта версии. Шрифт для подписей
                # ищется только локально (ultralytics_setup), без сети.
                **{key: value for key, value in options.items() if key != "device"},
            )
    except WorkerError:
        raise
    except Exception as exc:  # noqa: BLE001 — любой сбой обучения описывается в result.json
        traceback.print_exc(file=sys.stderr)
        oom = "out of memory" in str(exc).lower()
        raise WorkerError(
            ErrorCode.OUT_OF_MEMORY if oom else ErrorCode.TRAINING_FAILED,
            ("Не хватило памяти устройства: уменьши batch или imgsz. " if oom else "Обучение упало: ")
            + f"{type(exc).__name__}: {exc}"[:1000],
            {"epochsCompleted": progress.count},
        ) from None

    trainer = model.trainer
    publish_effective(trainer)
    # Ultralytics на первой эпохе сам уменьшает батч при нехватке памяти —
    # тогда lr0, посчитанный под исходный батч, уже не соответствует ему.
    actual_batch = int(getattr(trainer, "batch_size", p.batch) or p.batch)
    effective["effectiveBatch"] = actual_batch
    if actual_batch != p.batch and p.batch >= 1:
        ctx.warn(
            "batch_reduced",
            f"Ultralytics уменьшил батч с {p.batch} до {actual_batch} из-за нехватки памяти; "
            f"lr0 {effective['lr0']} посчитан под исходный батч",
        )

    # AMP запрошен, но trainer его отключил: CPU или GPU из чёрного списка
    # (ultralytics_setup._offline_check_amp). В паспорт идёт фактическое значение.
    actual_amp = bool(getattr(trainer, "amp", p.amp))
    effective["amp"] = actual_amp
    if p.amp not in (False, "fp32") and not actual_amp and p.device != "cpu":
        ctx.warn("amp_disabled", "AMP отключён: на этой видеокарте смешанная точность даёт NaN, обучение шло в fp32")

    ctx.stage("finalizing")
    best = Path(trainer.best)
    if not best.is_file() and Path(trainer.last).is_file():
        best = Path(trainer.last)
        effective["checkpointSelection"] = "last"
    else:
        effective["checkpointSelection"] = "best"
    if not best.is_file():
        raise WorkerError(ErrorCode.NO_WEIGHTS_PRODUCED, "Обучение не сохранило best.pt — ни одной завершённой эпохи")
    layout.artifacts_dir.mkdir(parents=True, exist_ok=True)
    best_artifact = layout.artifacts_dir / "best.pt"
    shutil.copyfile(best, best_artifact)
    from ultralytics import YOLO

    architecture.verify(p.architecture, YOLO(str(best_artifact)), "best.pt")

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
    # -1 — автовыбор: имя видеокарты станет известно, когда trainer выберет её (финальное окружение).
    if device not in ("cpu", "-1") and torch.cuda.is_available():
        env["deviceName"] = torch.cuda.get_device_name(int(device))
    return env
