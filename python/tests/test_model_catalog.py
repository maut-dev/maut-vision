"""Контракт одного каталога: допустимые архитектуры, шаг входа и признаки весов."""
import unittest
from types import SimpleNamespace

from pydantic import ValidationError
from worker.model_catalog import MODELS
from worker.protocol import DetectedArchitecture
from worker.protocol.request import TrainParams
from worker.operations.train.architecture import detect, matches


class ModelCatalogTests(unittest.TestCase):
    def test_all_catalog_models_are_accepted_by_protocol(self):
        self.assertEqual(len(MODELS), 59)
        for model_id in MODELS:
            with self.subTest(model_id=model_id):
                params = TrainParams(architecture=model_id, epochs=1, batch=1, imgsz=64, device="cpu", seed=0)
                self.assertEqual(params.architecture, model_id)

    def test_wrong_tasks_scales_and_families_are_rejected(self):
        for model_id, definition in MODELS.items():
            with self.subTest(model_id=model_id):
                found = DetectedArchitecture(yaml_file=definition["yamlNames"][0] + ".yaml", scale=definition["scale"], task="detect")
                self.assertTrue(matches(model_id, found))
                self.assertFalse(matches(model_id, found.model_copy(update={"task": "segment"})))
                self.assertFalse(matches(model_id, found.model_copy(update={"yaml_file": "foreign.yaml"})))
                if definition["scale"]:
                    self.assertFalse(matches(model_id, found.model_copy(update={"scale": "incorrect"})))
        with self.assertRaises(ValidationError):
            TrainParams(architecture="yolo27n", epochs=1, batch=1, imgsz=64, device="cpu", seed=0)

    def test_p6_requires_stride_64(self):
        with self.assertRaises(ValidationError):
            TrainParams(architecture="yolov5n6u", epochs=1, batch=1, imgsz=32, device="cpu", seed=0)
        TrainParams(architecture="yolo12n", epochs=1, batch=1, imgsz=32, device="cpu", seed=0)

    def test_legacy_metadata_comes_from_checkpoint_not_file_name(self):
        model = SimpleNamespace(model=SimpleNamespace(yaml={}), task="detect", ckpt={"train_args": {"model": "yolov8n.yaml"}})
        found = detect(model)
        self.assertTrue(matches("yolov8n", found))
        self.assertFalse(matches("yolo12n", found))


if __name__ == "__main__":
    unittest.main()
