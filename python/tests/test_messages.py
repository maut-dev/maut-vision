"""Texts of worker errors and warnings: keys, placeholders and what result.json and the events carry."""
from __future__ import annotations

import io
import json
import re
import unittest
from pathlib import Path

from worker.protocol import ErrorCode, ResultError, WorkerError
from worker.protocol.messages import MESSAGES, render

WORKER = Path(__file__).resolve().parents[1] / "worker"


class MessagesTest(unittest.TestCase):
    def test_a_key_is_a_text_or_a_group_never_both(self) -> None:
        codes = {key.split(".", 1)[0] for key in MESSAGES}
        for code in codes:
            leaf = code in MESSAGES
            grouped = any(key.startswith(code + ".") for key in MESSAGES)
            self.assertFalse(leaf and grouped, code)
        for key in MESSAGES:
            self.assertRegex(key, r"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)?$")

    def test_every_raised_error_code_has_a_text(self) -> None:
        sources = "\n".join(path.read_text(encoding="utf-8") for path in WORKER.rglob("*.py"))
        raised = {ErrorCode[name] for name in re.findall(r"ErrorCode\.([A-Z_]+)", sources) if name in ErrorCode.__members__}
        self.assertIn(ErrorCode.DATASET_INVALID, raised)
        for code in raised:
            self.assertTrue(any(key == code.value or key.startswith(code.value + ".") for key in MESSAGES), code)

    def test_every_reason_used_in_the_sources_has_a_text(self) -> None:
        sources = "\n".join(path.read_text(encoding="utf-8") for path in WORKER.rglob("*.py"))
        errors = re.findall(r'WorkerError\(\s*ErrorCode\.([A-Z_]+),\s*"([a-z_]+)"', sources)
        warnings = re.findall(r'\.warn\(\s*"([a-z_]+)"(?:,\s*"([a-z_]+)")?', sources)
        self.assertTrue(errors and warnings)
        for code, reason in errors:
            self.assertIn(f"{ErrorCode[code].value}.{reason}", MESSAGES)
        for code, reason in warnings:
            self.assertIn(f"{code}.{reason}" if reason else code, MESSAGES)

    def test_error_renders_english_message_and_string_args(self) -> None:
        error = WorkerError(ErrorCode.DEVICE_UNAVAILABLE, "gpu_missing", {"index": 1, "count": 0})
        self.assertEqual(error.message, "GPU 1 is requested, but 0 are visible")
        self.assertEqual(error.message_args, {"index": "1", "count": "0"})
        dumped = ResultError(code=error.code, message=error.message, details=error.details, reason=error.reason, args=error.message_args)
        payload = dumped.model_dump(mode="json", by_alias=True)
        self.assertEqual(payload["reason"], "gpu_missing")
        self.assertEqual(payload["args"], {"index": "1", "count": "0"})

    def test_unknown_key_or_missing_arg_fails_loudly(self) -> None:
        with self.assertRaises(KeyError):
            render("device_unavailable", "nope")
        with self.assertRaises(KeyError):
            render("device_unavailable", "gpu_missing", {"index": 1})

    def test_warning_event_carries_reason_and_args(self) -> None:
        from worker.operations.context import OperationContext
        from worker.runtime.events import EventEmitter

        stream = io.StringIO()
        ctx = OperationContext(layout=None, events=EventEmitter(stream))  # type: ignore[arg-type]
        ctx.warn("onnx_export_failed", "export_error", {"error": "RuntimeError: x"})
        event = json.loads(stream.getvalue().splitlines()[-1])
        self.assertEqual(
            (event["type"], event["code"], event["reason"], event["args"], event["message"]),
            ("warning", "onnx_export_failed", "export_error", {"error": "RuntimeError: x"}, "ONNX was not exported: RuntimeError: x"),
        )
        self.assertEqual(ctx.warnings[0].model_dump(mode="json", by_alias=True)["reason"], "export_error")


if __name__ == "__main__":
    unittest.main()
