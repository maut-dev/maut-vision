"""Коды ошибок result.json.

Код — машинная часть ошибки: по нему Domain Training решает, что показать
пользователю и можно ли что-то исправить. Сообщение — для человека, по-русски.
Строки кодов — часть протокола: переименование ломает C#-сторону (Core/Python).
"""
from __future__ import annotations

from enum import StrEnum
from typing import Any


class ErrorCode(StrEnum):
    # запрос
    REQUEST_NOT_FOUND = "request_not_found"
    REQUEST_UNREADABLE = "request_unreadable"
    REQUEST_INVALID = "request_invalid"
    PROTOCOL_UNSUPPORTED = "protocol_unsupported"
    OPERATION_NOT_IMPLEMENTED = "operation_not_implemented"
    # входные файлы
    INPUT_PATH_INVALID = "input_path_invalid"
    DATASET_INVALID = "dataset_invalid"
    MODEL_NOT_FOUND = "model_not_found"
    MODEL_LOAD_FAILED = "model_load_failed"
    ARCHITECTURE_MISMATCH = "architecture_mismatch"
    DEVICE_UNAVAILABLE = "device_unavailable"
    # исполнение
    OUT_OF_MEMORY = "out_of_memory"
    TRAINING_FAILED = "training_failed"
    EVALUATION_FAILED = "evaluation_failed"
    # проверки predict/live/screenshot
    SOURCE_INVALID = "source_invalid"
    REGION_INVALID = "region_invalid"
    PREDICTION_FAILED = "prediction_failed"
    CAPTURE_UNAVAILABLE = "capture_unavailable"
    NO_WEIGHTS_PRODUCED = "no_weights_produced"
    ARTIFACT_INVALID = "artifact_invalid"
    INTERNAL_ERROR = "internal_error"


class WorkerError(Exception):
    """Ожидаемый отказ: превращается в result.json со статусом failed.

    Всё, что работник может объяснить, поднимается этим исключением. Любое
    другое исключение — internal_error с трейсбеком только в stderr.
    """

    def __init__(self, code: ErrorCode, message: str, details: dict[str, Any] | None = None):
        super().__init__(message)
        self.code = code
        self.message = message
        self.details = details or {}
