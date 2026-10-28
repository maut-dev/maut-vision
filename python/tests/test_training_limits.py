import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from pydantic import ValidationError
from worker.protocol.request import LiveParams, TrainParams
from worker.operations.train.progress import TrainingProgress
from worker.operations.train.reports import write_console_log


class TrainingLimitsTest(unittest.TestCase):
    def test_large_valid_parameters_and_finite_numbers(self):
        params = TrainParams(architecture="yolo12n", epochs=10001, batch=2048, imgsz=8192, device="100", lr0=2, warmup_epochs=20000)
        self.assertEqual(params.epochs, 10001)
        live = LiveParams(architecture="yolo12n", imgsz=8192, device="100", monitor=100, fps=60, max_duration_sec=86400, conf=0, max_detections=10000)
        self.assertEqual(live.max_detections, 10000)
        for field, value in [("epochs", 0), ("batch", 0), ("lr0", float("inf")), ("lr0", 0), ("imgsz", 8193)]:
            values = dict(architecture="yolo12n", epochs=10001, batch=2048, imgsz=8192, device="100", lr0=2)
            values[field] = value
            with self.assertRaises(ValidationError):
                TrainParams(**values)

    def test_10001_epochs_append_durably_without_memory_history(self):
        with tempfile.TemporaryDirectory() as root:
            events = SimpleNamespace(emit=lambda *args, **kwargs: None)
            history = Path(root) / "history.jsonl"
            progress = TrainingProgress(events, 10001, history)
            trainer = SimpleNamespace(epoch=0, metrics={}, tloss=None, lr={"lr/pg0": 0.1}, fitness=0.5)
            with patch("worker.operations.train.progress.os.fsync") as sync:
                for epoch in range(10001):
                    trainer.epoch = epoch
                    progress._on_epoch_start(trainer)
                    progress._on_fit_epoch_end(trainer)
                self.assertEqual(sync.call_count, 10001)
            self.assertFalse(hasattr(progress, "history"))
            self.assertEqual(progress.count, 10001)
            with history.open() as saved:
                self.assertEqual(sum(1 for _ in saved), 10001)
            progress._on_fit_epoch_end(trainer)
            self.assertEqual(progress.count, 10001, "final best.pt validation is not a training epoch")

    def test_complete_console_log_beyond_old_8_mib_ceiling(self):
        with tempfile.TemporaryDirectory() as root:
            source = Path(root) / "console.raw.log"
            target = Path(root) / "training.log"
            with source.open("w", newline="") as raw:
                raw.write("first\rrewritten\r\n")
                for _ in range(9000):
                    raw.write("x" * 1024 + "\n")
                raw.write("last\n")
            self.assertEqual(write_console_log(source, target), target)
            self.assertGreater(target.stat().st_size, 8 * 1024 * 1024)
            with target.open() as log:
                self.assertEqual(log.readline(), "rewritten\n")
                last = ""
                for line in log:
                    last = line
                self.assertEqual(last, "last\n")


if __name__ == "__main__":
    unittest.main()
