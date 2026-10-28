"""Error codes of result.json.

The code is the machine part of an error: by it Core decides what the user can fix. The reason and the args choose the text the UI shows
(worker/protocol/messages.py); the English message goes to the logs. Code strings are part of the protocol: renaming one breaks the C#
side (Core/Python).
"""
from __future__ import annotations

from enum import StrEnum
from typing import Any


class ErrorCode(StrEnum):
    # request
    REQUEST_NOT_FOUND = "request_not_found"
    REQUEST_UNREADABLE = "request_unreadable"
    REQUEST_INVALID = "request_invalid"
    PROTOCOL_UNSUPPORTED = "protocol_unsupported"
    OPERATION_NOT_IMPLEMENTED = "operation_not_implemented"
    # input files
    INPUT_PATH_INVALID = "input_path_invalid"
    DATASET_INVALID = "dataset_invalid"
    MODEL_NOT_FOUND = "model_not_found"
    MODEL_LOAD_FAILED = "model_load_failed"
    ARCHITECTURE_MISMATCH = "architecture_mismatch"
    DEVICE_UNAVAILABLE = "device_unavailable"
    # execution
    OUT_OF_MEMORY = "out_of_memory"
    TRAINING_FAILED = "training_failed"
    EVALUATION_FAILED = "evaluation_failed"
    # checks: predict, live, screenshot
    SOURCE_INVALID = "source_invalid"
    REGION_INVALID = "region_invalid"
    PREDICTION_FAILED = "prediction_failed"
    CAPTURE_UNAVAILABLE = "capture_unavailable"
    NO_WEIGHTS_PRODUCED = "no_weights_produced"
    ARTIFACT_INVALID = "artifact_invalid"
    INTERNAL_ERROR = "internal_error"


class WorkerError(Exception):
    """An expected refusal: becomes result.json with the status failed.

    Everything the worker can explain is raised with this exception: the code, the reason of its message (None for a code with a single
    message) and the args of the message; the English message is rendered from worker/protocol/messages.py. details — machine data for
    the host. Any other exception is internal_error with the traceback only in stderr.
    """

    def __init__(
        self,
        code: ErrorCode,
        reason: str | None = None,
        args: dict[str, Any] | None = None,
        details: dict[str, Any] | None = None,
    ):
        from worker.protocol.messages import render

        message, values = render(code.value, reason, args)
        super().__init__(message)
        self.code = code
        self.reason = reason
        self.message_args = values
        self.message = message
        self.details = details or {}
