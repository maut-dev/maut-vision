"""Операция screenshot: снимок монитора для выбора рамки проверки с экрана.

Не грузит torch и ultralytics: только mss и OpenCV (кодирование PNG). Снимок
делается тем же захватом, что и live, поэтому координаты рамки совпадают.
"""
from __future__ import annotations

from typing import Any

from worker.operations.capture import Screen
from worker.operations.context import OperationContext
from worker.protocol import ErrorCode, Screenshot, ScreenshotRequest, WorkerError
from worker.runtime.job_layout import sha256_file


def run_screenshot(request: ScreenshotRequest, ctx: OperationContext) -> dict[str, Any]:
    import cv2

    ctx.stage("capturing")
    with Screen(request.params.monitor) as screen:
        frame = screen.grab(None)
        monitors = screen.monitors()
        index = screen.index
    ok, buffer = cv2.imencode(".png", frame)
    if not ok:
        raise WorkerError(ErrorCode.INTERNAL_ERROR, "Не удалось закодировать снимок экрана в PNG")
    ctx.layout.artifacts_dir.mkdir(parents=True, exist_ok=True)
    target = ctx.layout.artifacts_dir / "screen.png"
    target.write_bytes(buffer.tobytes())
    height, width = frame.shape[:2]
    return {
        "screenshot": Screenshot(
            path=ctx.layout.relative_to_output(target),
            size_bytes=target.stat().st_size,
            sha256=sha256_file(target),
            width=width,
            height=height,
            monitor=index,
            monitors=monitors,
        ),
    }


__all__ = ["run_screenshot"]
