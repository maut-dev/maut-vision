"""Entry point: maut-training-worker <jobDir>.

Exit codes (result.json decides the outcome anyway — code 0 without a valid
result does not count as success):
  0   — succeeded, result.json published
  1   — failed, result.json with an error published
  2   — invalid call: no job folder; there is nowhere to write the result
  3   — result.json could not be written
  4   — the job folder already has a result or artifacts: automatic repeat is forbidden
  130 — interrupted (Ctrl+C/Ctrl+Break); no result, as when killed
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
        raise WorkerError(ErrorCode.REQUEST_NOT_FOUND)
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise WorkerError(ErrorCode.REQUEST_UNREADABLE, args={"error": exc}) from None


def _raw_field(raw: Any, key: str) -> str | None:
    """A field of a request that failed validation — only so that the host
    can link the refusal to its operation. Only a short safe string is taken."""
    value = raw.get(key) if isinstance(raw, dict) else None
    if isinstance(value, str) and re.fullmatch(r"[A-Za-z0-9._-]{1,128}", value):
        return value
    return None


def main(argv: list[str] | None = None) -> int:
    # First of all: stdout is reserved for events, before any heavy imports.
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
        print("Usage: maut-training-worker <job folder>", file=sys.stderr)
        return EXIT_USAGE

    events = EventEmitter(events_stream)
    t0 = time.monotonic()
    started_at = utc_now()
    layout = JobLayout(Path(args[0]))
    attach_log_file(layout.logs_dir / "worker.log")
    print(f"[worker] pid {os.getpid()}, job {layout.job_dir}", file=sys.stderr, flush=True)

    def refuse_rerun(lay: JobLayout) -> bool:
        leftovers = lay.previous_run_leftovers()
        if leftovers:
            print(
                f"[worker] the job has already run ({', '.join(leftovers)}); "
                "a repeat is forbidden — Domain Training decides",
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
        print(f"[worker] refusal {exc.code}: {exc.message}", file=sys.stderr, flush=True)
        error = ResultError(code=exc.code, message=exc.message, details=exc.details, reason=exc.reason, args=exc.message_args)
    except KeyboardInterrupt:
        # Cancellation is not a failure: no result, as when the process is killed.
        print("[worker] interrupted", file=sys.stderr, flush=True)
        return EXIT_INTERRUPTED
    except Exception as exc:  # noqa: BLE001 — unexpected: traceback to stderr, code and message to result
        traceback.print_exc(file=sys.stderr)
        unexpected = WorkerError(ErrorCode.INTERNAL_ERROR, "unexpected", {"error": f"{type(exc).__name__}: {exc}"[:1000]})
        error = ResultError(
            code=unexpected.code, message=unexpected.message, reason=unexpected.reason, args=unexpected.message_args
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
    except Exception:  # noqa: BLE001 — without result.json the host still sees the failure by the exit code
        traceback.print_exc(file=sys.stderr)
        return EXIT_RESULT_NOT_WRITTEN

    events.emit("finished", status=status.value, errorCode=error.code.value if error else None)
    return EXIT_FAILED if error else EXIT_SUCCEEDED
