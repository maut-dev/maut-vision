"""Операция predict: веса версии по картинке или по видео из input/source.

Картинка: один кадр, событие frame всегда (с превью), в result — этот кадр.
Видео: чтение ПОСЛЕДОВАТЕЛЬНОЕ, пропускаемые кадры только grab() без
декодирования — перемотка (CAP_PROP_POS_FRAMES) в OpenCV на порядок медленнее
(по замеру). Событие frame — на каждый проанализированный кадр с
обнаружениями и, ради превью, на кадр без них не чаще previewIntervalMs;
progress — не чаще раза в секунду. Превью — файлы output/previews/ (previewFile).
Источник — жёсткая ссылка в input/ или абсолютный путь externalSource
(OpenCV 5 и np.fromfile открывают не-ASCII пути). Артефактов нет.
"""
from __future__ import annotations

import math
import os
import time
from typing import Any

from worker.operations.context import OperationContext
from worker.operations.predict import detections as common
from worker.protocol import ErrorCode, PredictedFrame, PredictRequest, SourceInfo, WorkerError


def _read_image(path: Any) -> Any:
    import cv2
    import numpy as np

    try:
        data = np.fromfile(str(path), dtype=np.uint8)
    except OSError as exc:
        raise WorkerError(ErrorCode.SOURCE_INVALID, f"Не удалось прочитать изображение: {exc}") from None
    # imdecode, а не imread: imread на Windows не открывает пути с кириллицей
    frame = cv2.imdecode(data, cv2.IMREAD_COLOR) if data.size else None
    if frame is None:
        raise WorkerError(ErrorCode.SOURCE_INVALID, "Файл не распознан как изображение (PNG, JPEG, WebP)")
    return frame


def _open_video(ctx: OperationContext, request: PredictRequest) -> Any:
    import cv2

    if request.input.external_source is not None:
        capture = cv2.VideoCapture(request.input.external_source, cv2.CAP_FFMPEG)
    else:
        # Относительный ASCII-путь из папки задачи: папка данных может быть с кириллицей.
        cwd = os.getcwd()
        os.chdir(ctx.layout.job_dir)
        try:
            capture = cv2.VideoCapture(request.input.source)
        finally:
            os.chdir(cwd)
    if not capture.isOpened():
        raise WorkerError(ErrorCode.SOURCE_INVALID, "Видео не открылось: формат не поддерживается OpenCV или файл повреждён")
    return capture


def _predict_image(request: PredictRequest, ctx: OperationContext, model: Any, device: Any, names: dict[int, str]) -> dict[str, Any]:
    p = request.params
    frame = _read_image(_source_path(ctx, request))
    height, width = frame.shape[:2]
    analyzed = common.crop(frame, p.region, "изображение")
    ctx.stage("predicting")
    started = time.monotonic()
    found = common.detect(model, analyzed, imgsz=p.imgsz, conf=p.conf, device=device, max_det=p.max_detections, names=names)
    record = PredictedFrame(index=0, time_ms=0, detections=found)
    summary = common.Summary()
    summary.add(found)
    common.emit_frame(
        ctx, record, analyzed.shape[1], analyzed.shape[0], common.write_preview(ctx, analyzed, 0, 1280), round((time.monotonic() - started) * 1000)
    )
    return {"summary": summary.model(), "source": SourceInfo(width=width, height=height), "frames": [record]}


def _predict_video(request: PredictRequest, ctx: OperationContext, model: Any, device: Any, names: dict[int, str]) -> dict[str, Any]:
    import cv2

    p = request.params
    capture = _open_video(ctx, request)
    try:
        fps = float(capture.get(cv2.CAP_PROP_FPS) or 0.0)
        if not math.isfinite(fps) or fps <= 0:
            ctx.warn("video_fps_unknown", "Видео не сообщает частоту кадров: время кадров посчитано для 30 к/с")
            fps = 30.0
        total = int(capture.get(cv2.CAP_PROP_FRAME_COUNT) or 0)
        width = int(capture.get(cv2.CAP_PROP_FRAME_WIDTH) or 0)
        height = int(capture.get(cv2.CAP_PROP_FRAME_HEIGHT) or 0)
        step = max(1, round(p.step_ms * fps / 1000))
        planned = math.ceil(total / step) if total > 0 else None
        duration_ms = round(total * 1000 / fps) if total > 0 else None
        summary = common.Summary()
        preview = common.PreviewClock(p.preview_interval_ms)
        last_progress = 0.0
        index = 0
        position_ms = 0
        ctx.stage("predicting")
        while True:
            if index % step:
                # пропуск: кадр читается из потока, но не декодируется
                if not capture.grab():
                    break
                index += 1
                continue
            ok, frame = capture.read()
            if not ok:
                break
            position_ms = round(index * 1000 / fps)
            analyzed = common.crop(frame, p.region, "кадр видео")
            started = time.monotonic()
            found = common.detect(model, analyzed, imgsz=p.imgsz, conf=p.conf, device=device, max_det=p.max_detections, names=names)
            summary.add(found)
            due = preview.due()
            if found or due:
                image = common.write_preview(ctx, analyzed, index) if due else None
                common.emit_frame(
                    ctx,
                    PredictedFrame(index=index, time_ms=position_ms, detections=found),
                    analyzed.shape[1],
                    analyzed.shape[0],
                    image,
                    round((time.monotonic() - started) * 1000),
                )
            now = time.monotonic()
            if now - last_progress >= 1.0:
                last_progress = now
                ctx.events.emit(
                    "progress",
                    framesProcessed=summary.frames_processed,
                    framesTotal=planned,
                    positionMs=position_ms,
                    durationMs=duration_ms,
                )
            index += 1
        if summary.frames_processed == 0:
            raise WorkerError(ErrorCode.SOURCE_INVALID, "Из видео не прочитано ни одного кадра")
        ctx.events.emit(
            "progress",
            framesProcessed=summary.frames_processed,
            framesTotal=summary.frames_processed,
            positionMs=position_ms,
            durationMs=duration_ms,
        )
        return {"summary": summary.model(), "source": SourceInfo(width=width, height=height, duration_ms=duration_ms, fps=round(fps, 3))}
    finally:
        capture.release()


def _source_path(ctx: OperationContext, request: PredictRequest) -> Any:
    from pathlib import Path

    if request.input.external_source is not None:
        return Path(request.input.external_source)
    return ctx.layout.resolve(request.input.source)


def run_predict(request: PredictRequest, ctx: OperationContext) -> dict[str, Any]:
    p = request.params
    ctx.stage("validating")
    source = _source_path(ctx, request)
    if not source.is_file():
        raise WorkerError(ErrorCode.SOURCE_INVALID, "Нет файла источника проверки")
    model, device, names = common.load(ctx, ctx.layout.resolve(request.input.model), p.architecture, p.device)
    fields = (_predict_image if p.source == "image" else _predict_video)(request, ctx, model, device, names)
    return {
        "effective_params": {
            "architecture": p.architecture,
            "imgsz": p.imgsz,
            "device": p.device,
            "conf": p.conf,
            "source": p.source,
            "stepMs": p.step_ms,
            "region": p.region.model_dump() if p.region else None,
        },
        **fields,
    }
