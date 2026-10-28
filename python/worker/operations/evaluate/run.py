"""Проверка весов на датасете: входы → model.val → метрики.

Та же последовательность отказов, что у обучения: датасет, устройство, веса и
архитектура проверяются до валидации. Артефактов операция не создаёт — только
числа в result.json. Графики ultralytics выключены (plots=False): они не входят
в контракт и тянут шрифт из сети.
"""
from __future__ import annotations

import sys
import traceback
from typing import Any

from worker.operations.context import OperationContext
from worker.operations.train import architecture, dataset, hyperparams, ultralytics_setup
from worker.operations.train.progress import map_metrics
from worker.operations.train.run import _check_device, _environment, _load_model
from worker.protocol import ClassMetrics, ErrorCode, EvaluateRequest, WorkerError


def _class_metrics(box: Any, names: dict[int, str] | list[str]) -> list[ClassMetrics]:
    """Метрики по классам из DetMetrics.box: только классы с объектами в выборке
    (ultralytics не считает AP для класса без разметки)."""
    out: list[ClassMetrics] = []
    indices = [int(i) for i in getattr(box, "ap_class_index", [])]
    for position, index in enumerate(indices):
        precision, recall, ap50, ap = (float(v) for v in box.class_result(position))
        values = {"precision": precision, "recall": recall, "mAP50": ap50, "mAP50_95": ap}
        clean = {k: round(v, 6) for k, v in values.items() if v == v and abs(v) != float("inf")}
        if isinstance(names, dict):
            name = names.get(index, str(index))
        else:
            name = names[index] if index < len(names) else str(index)
        out.append(ClassMetrics(index=index, name=str(name), metrics=clean))
    return out


def run_evaluate(request: EvaluateRequest, ctx: OperationContext) -> dict[str, Any]:
    p = request.params
    layout = ctx.layout

    ctx.stage("validating")
    data_yaml, dataset_summary = dataset.prepare(layout.resolve(request.input.dataset), layout.work_dir)
    if p.split == "test" and "imagesTest" not in dataset_summary:
        raise WorkerError(ErrorCode.DATASET_INVALID, "В датасете нет выборки test", {"key": "test"})
    workers = hyperparams.dataloader_workers()
    ultralytics_setup.configure(layout.work_dir)
    device = _check_device(p.device)

    ctx.stage("loading_model")
    model = _load_model(layout.resolve(request.input.model))
    detected = architecture.verify(p.architecture, model, "веса версии")

    ctx.stage("evaluating")
    try:
        result = model.val(
            data=str(data_yaml),
            split=p.split,
            imgsz=p.imgsz,
            batch=p.batch,
            device=device,
            workers=workers,
            project=str(layout.work_dir / "val"),
            name="val",
            exist_ok=True,
            plots=False,
            verbose=True,
        )
    except WorkerError:
        raise
    except Exception as exc:  # noqa: BLE001 — сбой проверки описывается в result.json
        traceback.print_exc(file=sys.stderr)
        oom = "out of memory" in str(exc).lower()
        raise WorkerError(
            ErrorCode.OUT_OF_MEMORY if oom else ErrorCode.EVALUATION_FAILED,
            ("Не хватило памяти устройства: уменьши batch. " if oom else "Проверка упала: ")
            + f"{type(exc).__name__}: {exc}"[:1000],
        ) from None

    metrics = map_metrics(getattr(result, "results_dict", None) or {})
    classes = _class_metrics(getattr(result, "box", None), getattr(result, "names", None) or {})
    return {
        "architecture": detected,
        "effective_params": {
            "architecture": p.architecture,
            "imgsz": p.imgsz,
            "batch": p.batch,
            "device": p.device,
            "split": p.split,
            "workers": workers,
            "dataset": dataset_summary,
        },
        "metrics": metrics,
        "class_metrics": classes,
        "environment": _environment(device),
    }
