"""Точка входа: maut-training-worker <jobDir>.

Коды выхода (итог всё равно решает result.json — код 0 без валидного
результата успехом не считается):
  0   — succeeded, result.json опубликован
  1   — failed, result.json с ошибкой опубликован
  2   — неверный вызов: нет папки задачи; результат писать некуда
  3   — result.json не удалось записать
  4   — в папке задачи уже есть результат или артефакты: автоповтор запрещён
  130 — прерван (Ctrl+C/Ctrl+Break); результата нет, как и при убийстве
"""
from __future__ import annotations

import json
import os
import re
import sys
import time
import traceback
from pathlib import Path
from typing import Any

from worker.runtime.streams import attach_log_file, take_over_stdout

EXIT_SUCCEEDED = 0
EXIT_FAILED = 1
EXIT_USAGE = 2
EXIT_RESULT_NOT_WRITTEN = 3
EXIT_ALREADY_RAN = 4
EXIT_INTERRUPTED = 130


def _read_request(path: Path) -> Any:
    from worker.protocol import ErrorCode, WorkerError

    if not path.is_file():
        raise WorkerError(ErrorCode.REQUEST_NOT_FOUND, "В папке задачи нет request.json")
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise WorkerError(ErrorCode.REQUEST_UNREADABLE, f"request.json не читается как JSON: {exc}") from None


def _raw_field(raw: Any, key: str) -> str | None:
    """Поле из непрошедшего проверку запроса — только чтобы host
    связала отказ со своей операцией. Берём лишь короткую безопасную строку."""
    value = raw.get(key) if isinstance(raw, dict) else None
    if isinstance(value, str) and re.fullmatch(r"[A-Za-z0-9._-]{1,128}", value):
        return value
    return None


def main(argv: list[str] | None = None) -> int:
    # Первым делом: stdout — только под события, до любых тяжёлых импортов.
    events_stream = take_over_stdout()

    from worker.operations import OperationContext, run_operation
    from worker.protocol import (
        PROTOCOL_VERSION,
        ErrorCode,
        JobResult,
        ResultError,
        ResultStatus,
        WorkerError,
        parse_request,
    )
    from worker.runtime import offline
    from worker.runtime.events import EventEmitter, utc_now
    from worker.runtime.job_layout import JobLayout, atomic_write_json

    args = sys.argv[1:] if argv is None else argv
    if len(args) != 1 or not Path(args[0]).is_dir():
        print("Использование: maut-training-worker <папка задачи>", file=sys.stderr)
        return EXIT_USAGE

    events = EventEmitter(events_stream)
    t0 = time.monotonic()
    started_at = utc_now()
    layout = JobLayout(Path(args[0]))
    attach_log_file(layout.logs_dir / "worker.log")
    print(f"[worker] pid {os.getpid()}, задача {layout.job_dir}", file=sys.stderr, flush=True)

    def refuse_rerun(lay: JobLayout) -> bool:
        leftovers = lay.previous_run_leftovers()
        if leftovers:
            print(
                f"[worker] задача уже исполнялась ({', '.join(leftovers)}); "
                "повтор запрещён — решение за Domain Training",
                file=sys.stderr,
                flush=True,
            )
        return bool(leftovers)

    if refuse_rerun(layout):
        return EXIT_ALREADY_RAN

    request = None
    raw: Any = None
    fields: dict[str, Any] = {}
    error: ResultError | None = None
    ctx: OperationContext | None = None
    try:
        raw = _read_request(layout.request_path)
        request = parse_request(raw)
        if request.output_root != layout.output_root:
            layout = layout.with_output_root(request.output_root)
            if refuse_rerun(layout):
                return EXIT_ALREADY_RAN
        events.emit(
            "started",
            operation=request.operation,
            operationId=request.operation_id,
            protocolVersion=PROTOCOL_VERSION,
            pid=os.getpid(),
        )
        offline.configure_environment(layout.work_dir / "ultralytics-config")
        offline.block_network()
        ctx = OperationContext(layout, events)
        fields = run_operation(request, ctx)
    except WorkerError as exc:
        print(f"[worker] отказ {exc.code}: {exc.message}", file=sys.stderr, flush=True)
        error = ResultError(code=exc.code, message=exc.message, details=exc.details)
    except KeyboardInterrupt:
        # Отмена — не сбой: результата нет, как и при убийстве процесса.
        print("[worker] прерван", file=sys.stderr, flush=True)
        return EXIT_INTERRUPTED
    except Exception as exc:  # noqa: BLE001 — непредвиденное: трейсбек в stderr, в result — код и сообщение
        traceback.print_exc(file=sys.stderr)
        error = ResultError(
            code=ErrorCode.INTERNAL_ERROR,
            message=f"Внутренняя ошибка работника: {type(exc).__name__}: {exc}"[:1000],
        )

    status = ResultStatus.FAILED if error else ResultStatus.SUCCEEDED
    try:
        result = JobResult(
            protocol_version=PROTOCOL_VERSION,
            operation_id=request.operation_id if request else _raw_field(raw, "operationId"),
            operation=request.operation if request else _raw_field(raw, "operation"),
            status=status,
            started_at=started_at,
            finished_at=utc_now(),
            duration_sec=round(time.monotonic() - t0, 2),
            error=error,
            warnings=ctx.warnings if ctx else [],
            **({} if error else fields),
        )
        atomic_write_json(layout.result_path, result.model_dump(mode="json", by_alias=True))
    except Exception:  # noqa: BLE001 — без result.json host всё равно увидит провал по коду выхода
        traceback.print_exc(file=sys.stderr)
        return EXIT_RESULT_NOT_WRITTEN

    events.emit("finished", status=status.value, errorCode=error.code.value if error else None)
    return EXIT_FAILED if error else EXIT_SUCCEEDED
