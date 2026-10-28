import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace

from pydantic import ValidationError

from worker.operations.capture import Screen
from worker.protocol import ErrorCode, WorkerError, parse_request
from worker.protocol.request import LiveParams, MonitorBounds, PredictInput


def predict(input_fields):
    return {
        "protocolVersion": 1,
        "operationId": "check-1",
        "operation": "predict",
        "input": {"model": "input/model/best.pt", **input_fields},
        "params": {"architecture": "yolo26n", "imgsz": 640, "device": "cpu", "source": "video", "stepMs": 500, "maxDetections": 5},
    }


class CheckRequestsTest(unittest.TestCase):
    def test_source_is_a_link_in_input_or_an_absolute_path(self):
        linked = parse_request(predict({"source": "input/source/source.mp4"}))
        self.assertEqual(linked.input.source, "input/source/source.mp4")
        self.assertEqual(linked.params.max_detections, 5)
        external = parse_request(predict({"externalSource": "D:\\Видео\\клип.mp4"}))
        self.assertIsNone(external.input.source)
        self.assertEqual(external.input.external_source, "D:\\Видео\\клип.mp4")
        for fields in ({}, {"source": "input/source/a.mp4", "externalSource": "D:\\a.mp4"}, {"externalSource": "relative\\a.mp4"},
                       {"externalSource": "D:\\x\\..\\a.mp4"}, {"source": "output/a.mp4"}):
            with self.assertRaises(WorkerError) as raised:
                parse_request(predict(fields))
            self.assertEqual(raised.exception.code, ErrorCode.REQUEST_INVALID)

    def test_live_names_the_monitor_it_expects(self):
        params = LiveParams(architecture="yolo26n", imgsz=640, device="0", expected_monitor=MonitorBounds(left=-1920, top=0, width=1920, height=1080))
        self.assertEqual(params.expected_monitor.left, -1920)
        with self.assertRaises(ValidationError):
            PredictInput(model="input/model/best.pt")

    def test_a_changed_monitor_is_refused(self):
        screen = SimpleNamespace(index=1, bounds={"left": 0, "top": 0, "width": 2560, "height": 1440})
        Screen.check_bounds(screen, MonitorBounds(left=0, top=0, width=2560, height=1440))
        Screen.check_bounds(screen, None)
        with self.assertRaises(WorkerError) as raised:
            Screen.check_bounds(screen, MonitorBounds(left=0, top=0, width=1920, height=1080))
        self.assertEqual(raised.exception.code, ErrorCode.CAPTURE_UNAVAILABLE)

    def test_preview_is_a_file_under_output(self):
        try:
            import numpy as np
        except ImportError:
            self.skipTest("numpy is not installed")
        from worker.operations.predict.detections import write_preview
        from worker.runtime.job_layout import JobLayout

        with tempfile.TemporaryDirectory() as root:
            ctx = SimpleNamespace(layout=JobLayout(Path(root)))
            frame = np.zeros((1080, 1920, 3), dtype=np.uint8)
            relative = write_preview(ctx, frame, 42)
            self.assertEqual(relative, "previews/frame-42.jpg")
            written = Path(root) / "output" / relative
            self.assertTrue(written.read_bytes().startswith(b"\xff\xd8"))
            self.assertEqual([path.name for path in written.parent.iterdir()], ["frame-42.jpg"])


if __name__ == "__main__":
    unittest.main()
