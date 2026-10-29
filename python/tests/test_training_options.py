import io
import json
import tempfile
import subprocess
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from pydantic import ValidationError

from worker.operations.train.options import resolved_options, trainer_snapshot
from worker.operations.train.dataset import select_training_validation_split
from worker.protocol import WorkerError
import yaml
from worker.operations.train.progress import TrainingProgress
from worker.protocol.request import TrainParams
from worker.runtime.events import EventEmitter
from worker.runtime.streams import _LiveLines, redact_console_paths


def params(**values):
    return TrainParams(architecture="yolo12n", epochs=2, batch=4, imgsz=32, device="cpu", **values)


class TrainingOptionsTest(unittest.TestCase):
    def test_production_entrypoint_dispatches_training_request_and_context(self):
        # Actual entrypoint and lazy dispatcher, no GPU or ML imports: controlled
        # dataset preflight refusal must not become a Python signature error.
        with tempfile.TemporaryDirectory() as root:
            job = Path(root)
            request = {"protocolVersion": 1, "operationId": "entrypoint-contract", "operation": "train",
                       "input": {"model": "input/model/yolo12n.pt"},
                       "params": {"architecture": "yolo12n", "epochs": 2, "batch": 4, "imgsz": 32, "device": "cpu"}}
            (job / "request.json").write_text(json.dumps(request), encoding="utf-8")
            completed = subprocess.run([sys.executable, "-m", "worker", str(job)],
                                       cwd=Path(__file__).resolve().parents[1], capture_output=True,
                                       text=True, encoding="utf-8", timeout=30)
            self.assertEqual(completed.returncode, 1, completed.stderr)
            result = json.loads((job / "output" / "result.json").read_text(encoding="utf-8"))
            self.assertEqual(result["error"]["code"], "dataset_invalid", completed.stderr)
            events = [json.loads(line) for line in completed.stdout.splitlines()]
            self.assertTrue(any(event["type"] == "stage" and event["stage"] == "validating" for event in events))

    def test_requested_validation_split_changes_only_working_loader_yaml(self):
        with tempfile.TemporaryDirectory() as root:
            working = Path(root) / "working.yaml"
            working.write_text("train: images/train\nval: images/val\ntest: images/test\n")
            select_training_validation_split(working, "test", {"imagesTrain": 4, "imagesVal": 2, "imagesTest": 1})
            self.assertEqual(yaml.safe_load(working.read_text())["val"], "images/test")
            with self.assertRaises(WorkerError):
                select_training_validation_split(working, "test", {"imagesTrain": 4, "imagesVal": 2})

    def test_detect_options_roundtrip_and_legacy_nested_augmentations(self):
        p = params(degrees=45, shear=180, scale=(0.8, 1.2), cutmix=0.5, bgr=0.25,
                   pretrained=False, val=False, save=False, cache="disk", classes=[0, 2],
                   freeze=[0, "23.cv2"], fraction=[0.8, 0.5, 0], amp="bf16", workers=0,
                   optimizer="MuSGD", channels_last=True)
        options, effective = resolved_options(p, 0)
        self.assertEqual(options["degrees"], 45)
        self.assertEqual(options["scale"], (0.8, 1.2))
        self.assertFalse(options["pretrained"])
        self.assertFalse(options["val"])
        self.assertFalse(options["save"])
        self.assertEqual(options["cache"], "disk")
        self.assertEqual(effective["ampRequested"], "bf16")
        self.assertTrue(effective["finalValidation"])
        self.assertTrue(effective["finalCheckpoint"])
        legacy, _ = resolved_options(params(augmentations={"degrees": 25}), 0)
        self.assertEqual(legacy["degrees"], 25)
        flat, _ = resolved_options(params(degrees=0, augmentations={"degrees": 25}), 0)
        self.assertEqual(flat["degrees"], 0, "explicit rotation off overrides legacy augmentation")

    def test_auto_batch_is_not_used_as_negative_or_fractional_lr_reference(self):
        for batch in [-1, 0.75]:
            p = TrainParams(architecture="yolo12n", epochs=2, batch=batch, imgsz=32, device="-1", optimizer="auto")
            options, effective = resolved_options(p, 0)
            self.assertEqual(options["batch"], batch)
            self.assertGreater(effective["lr0"], 0)
            optimizer = type("AdamW", (), {})()
            optimizer.param_groups = [{"lr": 0.002, "initial_lr": 0.002, "betas": (0.9, 0.999), "weight_decay": 0.001}]
            trainer = SimpleNamespace(batch_size=12, amp=False, device="cuda:0", optimizer=optimizer,
                                      args=SimpleNamespace(imgsz=32, warmup_bias_lr=0),
                                      train_loader=SimpleNamespace(num_workers=0), model=SimpleNamespace(names={0: "item"}))
            actual = trainer_snapshot(trainer, effective)
            self.assertEqual(actual["batch"], batch)
            self.assertEqual(actual["effectiveBatch"], 12)
            self.assertEqual(actual["optimizer"], "AdamW")
            self.assertEqual(actual["lr0"], 0.002)
            self.assertEqual(actual["lr0Source"], "optimizer-auto")
            self.assertEqual(actual["outputClasses"], [{"index": 0, "name": "item"}])

    def test_reject_invalid_variants_and_uncontrolled_paths(self):
        for values in [{"batch": 0}, {"batch": 1.5}, {"batch": -2}, {"scale": [-1, 1]},
                       {"freeze": ["../weights"]}, {"classes": []}, {"classes": [0, 0]},
                       {"fraction": [0.5, 0]}, {"pretrained": "C:/weights.pt"},
                       {"workers": -1}, {"compile": "shell-command"}, {"cfg": "C:/config.yaml"},
                       {"degrees": float("inf")}]:
            base = dict(architecture="yolo12n", epochs=2, batch=4, imgsz=32, device="cpu") | values
            with self.assertRaises(ValidationError, msg=str(values)):
                TrainParams(**base)

    def test_live_console_is_complete_chunked_and_preserves_event_order(self):
        stream = io.StringIO()
        events = EventEmitter(stream)
        lines = _LiveLines(lambda line: events.emit("log", message=line))
        lines.write("\x1b[31mactual console\x1b[0m\rnext\n")
        lines.write("x" * 9000)
        lines.flush()
        records = [json.loads(line) for line in stream.getvalue().splitlines()]
        self.assertEqual(records[0]["message"], "actual console")
        self.assertEqual(records[1]["message"], "next")
        self.assertEqual("".join(record["message"] for record in records[2:]), "x" * 9000)
        self.assertTrue(all(len(record["message"]) <= 2000 for record in records))
        self.assertEqual([record["sequence"] for record in records], list(range(1, len(records) + 1)))

    def test_live_logs_redact_job_and_machine_paths(self):
        message = r"data=C:\private\dataset\data.yaml model='C:\User Name\weights.pt' other=\\server\private\last.pt"
        sanitized = redact_console_paths(message)
        self.assertNotIn("private", sanitized)
        self.assertNotIn("User Name", sanitized)
        self.assertNotIn("server", sanitized)
        self.assertIn("[local path]", sanitized)
        self.assertEqual(redact_console_paths("Loading /home/user/model.pt"), "Loading [local path]")
        self.assertEqual(redact_console_paths("Loading /jobs/run/input/model.pt", Path("/jobs/run")), "Loading [run folder][local path]")

    def test_epoch_fitness_and_time_budget_epoch_total_are_live(self):
        with tempfile.TemporaryDirectory() as root:
            stream = io.StringIO()
            history = Path(root) / "history.jsonl"
            progress = TrainingProgress(EventEmitter(stream), 2, history)
            trainer = SimpleNamespace(epoch=2, epochs=5, metrics={"metrics/mAP50(B)": 0.6},
                                      tloss=None, lr={"lr/pg0": 0.002}, fitness=0.25)
            progress._on_epoch_start(trainer)
            progress._on_fit_epoch_end(trainer)
            record = json.loads(stream.getvalue())
            self.assertEqual(record["epochs"], 5)
            self.assertEqual(record["fitness"], 0.25)
            self.assertEqual(json.loads(history.read_text())["fitness"], 0.25)

    def test_disabled_intermediate_validation_does_not_repeat_old_metrics(self):
        with tempfile.TemporaryDirectory() as root:
            stream = io.StringIO()
            progress = TrainingProgress(EventEmitter(stream), 10, Path(root) / "history.jsonl")
            trainer = SimpleNamespace(epoch=2, epochs=10, args=SimpleNamespace(val=False),
                                      metrics={"metrics/mAP50(B)": 0.6}, tloss=None, lr={}, fitness=0.25, stop=False)
            progress._on_epoch_start(trainer)
            progress._on_train_epoch_end(trainer)
            progress._on_fit_epoch_end(trainer)
            record = json.loads(stream.getvalue())
            self.assertEqual(record["metrics"], {})
            self.assertIsNone(record["fitness"])


if __name__ == "__main__":
    unittest.main()
