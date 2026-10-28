"""Worker operations. Each receives a validated request and the job context
and returns the result.json fields; a refusal is WorkerError."""
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
        # import here: torch and ultralytics take seconds to load, and a refusal of
        # a broken request should not wait for them
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
        # without torch/ultralytics: a screenshot is needed in a second, not after the network loads
        from worker.operations.screenshot import run_screenshot

        return run_screenshot(request, ctx)
    # export is already in the protocol so that the host builds requests
    # by the final schema; execution is the next step
    raise WorkerError(
        ErrorCode.OPERATION_NOT_IMPLEMENTED,
        args={"operation": request.operation},
        details={"operation": request.operation, "implemented": ["train", "evaluate", "predict", "live", "screenshot"]},
    )


__all__ = ["OperationContext", "run_operation"]
