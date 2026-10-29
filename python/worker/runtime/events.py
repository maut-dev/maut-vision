"""Progress events: one JSON line per event, in stdout.

Every event carries type and sequence. sequence grows monotonically from 1 within
one run — the parser uses it to drop repeats and notice gaps.
Lines are short: the parser limits the line length (64 KiB, 2 MiB for frame because of
a long list of detections), so anything large (epoch history, artifact paths)
goes only into result.json, and frame previews go as files in output/previews/.

Event types:
  started   — operation, operationId, protocolVersion, pid
  stage     — stage: validating | loading_model | training | evaluating | predicting | capturing | exporting | finalizing
  batch     — epoch, batch, batches (at most once per second)
  epoch     — epoch, epochs, losses, metrics, lr, fitness
  effectiveParams — parameters, capture: requested | trainer; before/following trainer initialization
  log       — actual console line, up to 2000 characters; machine paths redacted
  environment — train: environment {python, torch, ultralytics, cuda, cudaAvailable, deviceName?} before training
  warning   — code, message
  frame     — predict/live: index, timeMs, width, height, detections[{classIndex, name,
              confidence, box{x,y,width,height} as fractions of the frame}], inferenceMs, previewFile?
              (output-relative previews/frame-<index>.jpg, no more often than previewIntervalMs; the host
              reads and deletes the file)
  progress  — predict on a video: framesProcessed, framesTotal|null, positionMs, durationMs|null
  finished  — status, errorCode
"""
from __future__ import annotations

import json
import os
import sys
import threading
import time
from datetime import datetime, timezone
from typing import Any, TextIO


# Matches EXIT_INTERRUPTED in worker/main.py: interrupted, no result.
EXIT_PARENT_GONE = 130


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")


class EventEmitter:
    def __init__(self, stream: TextIO):
        self._stream = stream
        self._sequence = 0
        self._lock = threading.Lock()
        self._last_batch_at = 0.0

    def emit(self, event_type: str, **fields: Any) -> None:
        with self._lock:
            self._sequence += 1
            payload = {"type": event_type, "sequence": self._sequence, "ts": utc_now(), **fields}
            try:
                self._stream.write(json.dumps(payload, ensure_ascii=False, separators=(",", ":")) + "\n")
                self._stream.flush()
            except (OSError, ValueError):
                # QA: the parent closed the pipe — the application died. Domain Training does not
                # pick such a run up again (on start it becomes interrupted, result.json is not read),
                # so further training of the orphan would only occupy the GPU. Exit as on interruption:
                # no result. On Windows the libuv job object kills the orphan earlier than this.
                print("[worker] the event pipe is closed: the application has exited, the worker is stopping", file=sys.stderr, flush=True)
                os._exit(EXIT_PARENT_GONE)

    def batch(self, epoch: int, batch: int, batches: int) -> None:
        """Progress inside an epoch with a rate limit: a long epoch must not
        look like a hang, but there is no reason to flood the pipe either."""
        now = time.monotonic()
        if batch < batches and now - self._last_batch_at < 1.0:
            return
        self._last_batch_at = now
        self.emit("batch", epoch=epoch, batch=batch, batches=batches)
