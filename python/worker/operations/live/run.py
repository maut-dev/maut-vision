"""The live operation: capture of the screen crop → predict → frame event, until cancelled.

The normal stop is external: Domain Training ends the process tree
(check cancellation), and there is no result.json then. The safeguard against a forgotten check is
maxDurationSec: on it the loop exits by itself and writes the summary. A closed event
pipe (the application died) stops the worker on the very next frame.

A frame event is sent for every frame, even an empty one: the interface shows "nothing
right now". Preview — a file in output/previews/ (previewFile) no more often than previewIntervalMs.
expectedMonitor is compared before the model loads: the crop was chosen on a screenshot of this
monitor.
"""
from __future__ import annotations

import time
from typing import Any

from worker.operations.capture import Screen
from worker.operations.context import OperationContext
from worker.operations.predict import detections as common
from worker.protocol import LiveRequest, PredictedFrame, SourceInfo


def run_live(request: LiveRequest, ctx: OperationContext) -> dict[str, Any]:
    p = request.params
    ctx.stage("validating")
    with Screen(p.monitor) as screen:
        screen.check_bounds(p.expected_monitor)
        screen.check(p.region)
        model, device, names = common.load(ctx, ctx.layout.resolve(request.input.model), p.architecture, p.device)
        summary = common.Summary()
        preview = common.PreviewClock(p.preview_interval_ms)
        period = 1.0 / p.fps
        ctx.stage("capturing")
        started = time.monotonic()
        index = 0
        width = height = 0
        while time.monotonic() - started < p.max_duration_sec:
            tick = time.monotonic()
            frame = screen.grab(p.region)
            height, width = frame.shape[:2]
            found = common.detect(model, frame, imgsz=p.imgsz, conf=p.conf, device=device, max_det=p.max_detections, names=names)
            summary.add(found)
            inference_ms = round((time.monotonic() - tick) * 1000)
            common.emit_frame(
                ctx,
                PredictedFrame(index=index, time_ms=round((tick - started) * 1000), detections=found),
                width,
                height,
                common.write_preview(ctx, frame, index) if preview.due() else None,
                inference_ms,
            )
            index += 1
            left = period - (time.monotonic() - tick)
            if left > 0:
                time.sleep(left)
        ctx.warn("live_time_limit", args={"seconds": p.max_duration_sec})
        return {
            "effective_params": {
                "architecture": p.architecture,
                "imgsz": p.imgsz,
                "device": p.device,
                "conf": p.conf,
                "monitor": screen.index,
                "region": p.region.model_dump() if p.region else None,
                "fps": p.fps,
                "maxDurationSec": p.max_duration_sec,
            },
            "summary": summary.model(),
            "source": SourceInfo(width=width, height=height),
        }
