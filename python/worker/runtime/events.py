"""События прогресса: одна строка JSON на событие, в stdout.

Каждое событие несёт type и sequence. sequence монотонно растёт с 1 внутри
одного запуска — по нему парсер отбрасывает повторы и видит пропуски.
Строки короткие: парсер ограничивает длину строки (64 КиБ, у frame — 2 МиБ ради
длинного списка обнаружений), поэтому крупное (история эпох, пути артефактов)
идёт только в result.json, а превью кадров — файлами output/previews/.

Типы событий:
  started   — operation, operationId, protocolVersion, pid
  stage     — stage: validating | loading_model | training | evaluating | predicting | capturing | exporting | finalizing
  batch     — epoch, batch, batches (не чаще раза в секунду)
  epoch     — epoch, epochs, losses, metrics, lr, fitness
  effectiveParams — parameters, capture: requested | trainer; before/following trainer initialization
  log       — actual console line, up to 2000 characters; machine paths redacted
  environment — train: environment {python, torch, ultralytics, cuda, cudaAvailable, deviceName?} до обучения
  warning   — code, message
  frame     — predict/live: index, timeMs, width, height, detections[{classIndex, name,
              confidence, box{x,y,width,height} в долях кадра}], inferenceMs, previewFile?
              (output-relative previews/frame-<index>.jpg, не чаще previewIntervalMs; host
              читает и удаляет файл)
  progress  — predict по видео: framesProcessed, framesTotal|null, positionMs, durationMs|null
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


# Совпадает с EXIT_INTERRUPTED в worker/main.py: прерван, результата нет.
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
                # QA: родитель закрыл пайп — приложение умерло. Domain Training такой запуск не
                # переподхватывает (при старте он станет interrupted, result.json не читается),
                # поэтому дообучение сироты только занимало бы GPU. Выходим как при прерывании:
                # результата нет. На Windows сироту раньше этого убивает job object libuv.
                print("[worker] пайп событий закрыт: приложение завершилось, работник останавливается", file=sys.stderr, flush=True)
                os._exit(EXIT_PARENT_GONE)

    def batch(self, epoch: int, batch: int, batches: int) -> None:
        """Прогресс внутри эпохи с ограничением частоты: длинная эпоха не
        должна выглядеть зависанием, но и засыпать пайп незачем."""
        now = time.monotonic()
        if batch < batches and now - self._last_batch_at < 1.0:
            return
        self._last_batch_at = now
        self.emit("batch", epoch=epoch, batch=batch, batches=batches)
