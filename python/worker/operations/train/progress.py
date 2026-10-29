"""Training progress from ultralytics callbacks.

Callbacks, not polling results.csv: the file is written whole at the end of an epoch, and
reading it in pursuit races with the writer. Callbacks see the same numbers at the same
moment.

Trap: after the loop ultralytics validates best.pt and calls
on_fit_epoch_end once more — with epoch+1. This is not an epoch but the final evaluation; we tell it apart
by the absence of on_train_epoch_start before it.
"""
from __future__ import annotations

import math
import os
from pathlib import Path
from typing import Any

from worker.protocol import EpochRecord
from worker.runtime.events import EventEmitter

# ultralytics metric keys → protocol names
_METRIC_NAMES = {
    "metrics/precision(B)": "precision",
    "metrics/recall(B)": "recall",
    "metrics/mAP50(B)": "mAP50",
    "metrics/mAP50-95(B)": "mAP50_95",
    "val/box_loss": "valBoxLoss",
    "val/cls_loss": "valClsLoss",
    "val/dfl_loss": "valDflLoss",
}


def _clean(value: Any) -> float | None:
    try:
        f = float(value)
    except (TypeError, ValueError):
        return None
    return round(f, 6) if math.isfinite(f) else None


def map_metrics(raw: dict[str, Any]) -> dict[str, float]:
    out = {}
    for key, name in _METRIC_NAMES.items():
        v = _clean(raw.get(key))
        if v is not None:
            out[name] = v
    return out


class TrainingProgress:
    def __init__(self, events: EventEmitter, epochs: int, history_path: Path):
        self.events = events
        self.epochs = epochs
        self.count = 0
        self.history_path = history_path
        history_path.parent.mkdir(parents=True, exist_ok=True)
        history_path.write_text("", encoding="utf-8")
        self.final_metrics: dict[str, float] | None = None
        self._in_epoch = False
        self._batch = 0
        self._validation_this_epoch = True

    def attach(self, model: Any) -> None:
        model.add_callback("on_train_epoch_start", self._on_epoch_start)
        model.add_callback("on_train_batch_end", self._on_batch_end)
        model.add_callback("on_train_epoch_end", self._on_train_epoch_end)
        model.add_callback("on_fit_epoch_end", self._on_fit_epoch_end)

    def _on_epoch_start(self, trainer: Any) -> None:
        self._in_epoch = True
        self._batch = 0
        self.epochs = int(getattr(trainer, "epochs", self.epochs))

    def _on_batch_end(self, trainer: Any) -> None:
        self._batch += 1
        batches = len(trainer.train_loader) if getattr(trainer, "train_loader", None) is not None else 0
        self.events.batch(epoch=int(trainer.epoch) + 1, batch=self._batch, batches=batches)

    def _on_fit_epoch_end(self, trainer: Any) -> None:
        metrics = map_metrics(getattr(trainer, "metrics", None) or {})
        if not self._in_epoch:
            self.final_metrics = metrics
            return
        self._in_epoch = False
        if not self._validation_this_epoch:
            metrics = {}
        losses: dict[str, float] = {}
        if getattr(trainer, "tloss", None) is not None:
            for k, v in trainer.label_loss_items(trainer.tloss, prefix="train").items():
                c = _clean(v)
                if c is not None:
                    losses[k.split("/", 1)[-1]] = c
        lr = _clean((getattr(trainer, "lr", None) or {}).get("lr/pg0"))
        # fitness is already computed by validate() before this callback; best.pt is saved by its maximum
        fitness = _clean(getattr(trainer, "fitness", None)) if self._validation_this_epoch else None
        record = EpochRecord(epoch=int(trainer.epoch) + 1, losses=losses, metrics=metrics, lr=lr, fitness=fitness)
        # Persist before notifying the backend; a failed/interrupted run keeps every complete epoch.
        with self.history_path.open("a", encoding="utf-8", newline="\n") as history:
            history.write(record.model_dump_json(by_alias=True) + "\n")
            history.flush()
            os.fsync(history.fileno())
        self.count += 1
        self.events.emit(
            "epoch",
            epoch=record.epoch,
            epochs=self.epochs,
            losses=record.losses,
            metrics=record.metrics,
            lr=record.lr,
            fitness=record.fitness,
        )

    def _on_train_epoch_end(self, trainer: Any) -> None:
        # Ultralytics retains trainer.metrics between validations. Emit only values
        # actually measured for this epoch instead of copying the preceding one.
        self._validation_this_epoch = bool(
            getattr(getattr(trainer, "args", None), "val", True)
            or int(trainer.epoch) + 1 >= int(getattr(trainer, "epochs", self.epochs))
            or getattr(getattr(trainer, "stopper", None), "possible_stop", False)
            or getattr(trainer, "stop", False)
        )
