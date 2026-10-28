"""Операции работника. Каждая получает проверенный запрос и контекст задачи
и возвращает поля result.json; отказ — WorkerError."""
from __future__ import annotations

from typing import Any

from worker.operations.context import OperationContext
from worker.protocol import (
    ErrorCode,
    EvaluateRequest,
    ExportRequest,
    LiveRequest,
    PredictRequest,
    ScreenshotRequest,
    TrainRequest,
    WorkerError,
)


def run_operation(
    request: TrainRequest | EvaluateRequest | ExportRequest | PredictRequest | LiveRequest | ScreenshotRequest,
    ctx: OperationContext,
) -> dict[str, Any]:
    if isinstance(request, TrainRequest):
        # импорт здесь: torch и ultralytics грузятся секунды, и отказ по
        # битому запросу не должен их ждать
        from worker.operations.train import run_train

        return run_train(request, ctx)
    if isinstance(request, EvaluateRequest):
        from worker.operations.evaluate import run_evaluate

        return run_evaluate(request, ctx)
    if isinstance(request, PredictRequest):
        from worker.operations.predict import run_predict

        return run_predict(request, ctx)
    if isinstance(request, LiveRequest):
        from worker.operations.live import run_live

        return run_live(request, ctx)
    if isinstance(request, ScreenshotRequest):
        # без torch/ultralytics: снимок нужен за секунду, а не за время загрузки сети
        from worker.operations.screenshot import run_screenshot

        return run_screenshot(request, ctx)
    # export уже есть в протоколе, чтобы host строил запросы
    # по окончательной схеме; исполнение — следующим шагом
    raise WorkerError(
        ErrorCode.OPERATION_NOT_IMPLEMENTED,
        f"Операция '{request.operation}' есть в протоколе, но в этой версии работника не реализована",
        {"operation": request.operation, "implemented": ["train", "evaluate", "predict", "live", "screenshot"]},
    )


__all__ = ["OperationContext", "run_operation"]
