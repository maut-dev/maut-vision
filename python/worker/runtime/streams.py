"""stdout carries only protocol events, everything else goes to stderr and logs/.

Ultralytics, torch and tqdm write whatever they like to stdout: banners, tables,
progress bars. The event parser on the host side would take them for broken
protocol lines. So before the ultralytics import the real stdout (fd 1)
is moved to a separate descriptor for events only, and fd 1 and sys.stdout
are redirected to stderr. Even native code writing directly to fd 1 does not
get into the events.
"""
from __future__ import annotations

import io
import os
import sys
from collections.abc import Iterator
from contextlib import contextmanager
from pathlib import Path
from typing import TextIO
from collections.abc import Callable
import re


class _Tee(io.TextIOBase):
    """stderr + log file. The log must be UTF-8: ultralytics writes emoji,
    and on Windows a file opens in cp1251 by default, and writing used to fail."""

    def __init__(self, *targets: TextIO):
        self._targets = list(targets)

    def add(self, target: TextIO) -> None:
        self._targets.append(target)

    def remove(self, target: TextIO) -> None:
        if target in self._targets:
            self._targets.remove(target)

    def write(self, s: str) -> int:
        for t in self._targets:
            try:
                t.write(s)
            except (OSError, ValueError):
                # a closed stderr (the parent died) must not crash training
                pass
        return len(s)

    def flush(self) -> None:
        for t in self._targets:
            try:
                t.flush()
            except (OSError, ValueError):
                pass

    @property
    def encoding(self) -> str:  # type: ignore[override]
        return "utf-8"

    def isatty(self) -> bool:
        return False


def take_over_stdout() -> TextIO:
    """Take stdout for events and return the stream for them.

    Call it first, before importing heavy libraries: ultralytics
    remembers sys.stdout in its logger at import.
    """
    events_fd = os.dup(1)
    events = os.fdopen(events_fd, "w", encoding="utf-8", newline="\n", buffering=1)
    sys.stdout.flush()
    os.dup2(2, 1)
    # stderr in a pipe on Windows is opened in the console encoding — emoji from
    # ultralytics would break writing. replace: diagnostics are not worth a crash.
    if hasattr(sys.stderr, "reconfigure"):
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    sys.stdout = sys.stderr
    return events


_tee: _Tee | None = None


def redact_console_paths(message: str, job_root: Path | None = None) -> str:
    """Public live logs identify the job, not machine directories. Full file logs remain local."""
    if job_root is not None:
        root = str(job_root)
        for variant in {root, root.replace("\\", "/"), root.replace("/", "\\")}:
            message = re.sub(re.escape(variant), "[run folder]", message, flags=re.IGNORECASE)
    # Quoted paths may contain spaces; bare paths end at whitespace or argument punctuation.
    message = re.sub(r"(['\"])(?:[A-Za-z]:[\\/]|\\\\|/)[^'\"\r\n]*\1", "[local path]", message)
    message = re.sub(r"(?<![A-Za-z0-9])(?:[A-Za-z]:[\\/]|\\\\)[^\s,;\]\[{}()'\"]+", "[local path]", message)
    message = re.sub(r"(?<![A-Za-z0-9:/])/(?:[A-Za-z0-9_.-]+/)+[^\s,;\]\[{}()'\"]*", "[local path]", message)
    return message


class _LiveLines(io.TextIOBase):
    """Emit real console lines in small chunks while the full log stays on disk."""
    def __init__(self, on_line: Callable[[str], None]):
        self._on_line = on_line
        self._pending = ""

    def write(self, value: str) -> int:
        self._pending += re.sub(r"\x1b\[[0-?]*[ -/]*[@-~]", "", value)
        while self._pending:
            match = re.search(r"[\r\n]", self._pending)
            end = match.start() if match else min(2000, len(self._pending))
            if match is None and len(self._pending) < 2000:
                break
            line = self._pending[:end]
            self._pending = self._pending[end + 1:] if match else self._pending[end:]
            for index in range(0, len(line), 2000):
                chunk = line[index:index + 2000]
                if chunk.strip():
                    self._on_line(chunk)
        return len(value)

    def flush(self) -> None:
        if self._pending.strip():
            self._on_line(self._pending)
        self._pending = ""


def attach_log_file(log_path: Path) -> None:
    """Duplicate diagnostics into logs/worker.log (UTF-8)."""
    global _tee
    log_path.parent.mkdir(parents=True, exist_ok=True)
    fh = log_path.open("a", encoding="utf-8", errors="replace", buffering=1)
    _tee = _Tee(sys.__stderr__ or sys.stderr, fh)
    sys.stderr = _tee  # type: ignore[assignment]
    sys.stdout = _tee  # type: ignore[assignment]


@contextmanager
def capture_console(path: Path, on_line: Callable[[str], None] | None = None) -> Iterator[None]:
    """Also write the console output into a separate file for the duration of the block.

    This is the training console log for the version passport: what ultralytics
    prints (tables, epoch progress), not the event protocol — stdout
    stays reserved for events. The ultralytics logger remembers
    sys.stdout at import, that is this same _Tee, so the new sink sees it too.
    The file is written as is (with \r of progress bars); it is cleaned by the side
    that turns the draft into a report file.
    """
    global _tee
    path.parent.mkdir(parents=True, exist_ok=True)
    fh = path.open("w", encoding="utf-8", errors="replace", newline="")
    if _tee is None:
        _tee = _Tee(sys.__stderr__ or sys.stderr)
        sys.stderr = _tee  # type: ignore[assignment]
        sys.stdout = _tee  # type: ignore[assignment]
    tee = _tee
    tee.add(fh)
    live = _LiveLines(on_line) if on_line is not None else None
    if live is not None:
        tee.add(live)
    try:
        yield
    finally:
        tee.flush()
        tee.remove(fh)
        if live is not None:
            tee.remove(live)
        fh.close()
