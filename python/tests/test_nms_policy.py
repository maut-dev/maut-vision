import importlib.util
import math
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

spec = importlib.util.spec_from_file_location(
    "nms_policy", Path(__file__).parents[1] / "worker/operations/predict/nms_policy.py"
)
policy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(policy)


class Tensor:
    def __init__(self, shape, extent=20000):
        self.shape = shape
        self.extent = extent

    def __getitem__(self, _key):
        return self

    def abs(self):
        return self

    def max(self):
        return self

    def item(self):
        return self.extent

    def numel(self):
        return 4


def original(prediction, max_det=300, max_nms=30000, max_time_img=0.05,
             max_wh=7680, multi_label=False, nc=0, end2end=False):
    return dict(max_det=max_det, max_nms=max_nms, max_time_img=max_time_img, max_wh=max_wh)


class NmsPolicyTest(unittest.TestCase):
    def test_actual_candidate_capacity_and_coordinate_span(self):
        module = SimpleNamespace(non_max_suppression=original)
        with patch.object(policy, "import_module", return_value=module):
            with policy.complete_nms(None, 8192):
                result = module.non_max_suppression(Tensor((1, 84, 40001)), max_det=100)
                self.assertEqual(result["max_det"], 40001)
                self.assertEqual(result["max_nms"], 40001)
                self.assertTrue(math.isinf(result["max_time_img"]))
                self.assertGreater(result["max_wh"], 80000)
            self.assertIs(module.non_max_suppression, original)

    def test_multilabel_expansion_and_explicit_user_limit(self):
        module = SimpleNamespace(non_max_suppression=original)
        with patch.object(policy, "import_module", return_value=module):
            with policy.complete_nms(17, 32):
                result = module.non_max_suppression([Tensor((1, 7, 8))], multi_label=True)
                self.assertEqual(result["max_det"], 17)
                self.assertEqual(result["max_nms"], 24)

    def test_end_to_end_shape_uses_existing_model_rows(self):
        module = SimpleNamespace(non_max_suppression=original)
        with patch.object(policy, "import_module", return_value=module):
            with policy.complete_nms(None, 32):
                result = module.non_max_suppression(Tensor((1, 700, 6)))
                self.assertEqual(result["max_det"], 700)

    def test_library_function_restored_after_inference_failure(self):
        module = SimpleNamespace(non_max_suppression=original)
        with patch.object(policy, "import_module", return_value=module):
            with self.assertRaisesRegex(RuntimeError, "inference failed"):
                with policy.complete_nms(None, 32):
                    raise RuntimeError("inference failed")
            self.assertIs(module.non_max_suppression, original)


if __name__ == "__main__":
    unittest.main()
