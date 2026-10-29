"""Layout of the job folder and safe writing into it.

training/jobs/<jobId>/
  request.json
  input/{dataset/, model/, manifest.json}   — read only
  output/{result.json, artifacts/, work/}   — written by the worker
  logs/                                     — diagnostics

output/work/ — the ultralytics draft (results.csv, last.pt, settings): it
is not part of the contract and is not registered. Finished results lie only in
artifacts/ and are listed in result.json.
"""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
from typing import Any

from worker.protocol.errors import ErrorCode, WorkerError

REQUEST_FILE = "request.json"
RESULT_FILE = "result.json"
DEFAULT_OUTPUT_ROOT = "output"


class JobLayout:
    def __init__(self, job_dir: Path, output_root: str = DEFAULT_OUTPUT_ROOT):
        self.job_dir = job_dir.resolve()
        self.output_root = output_root
        self.output_dir = self.resolve(output_root)

    @property
    def request_path(self) -> Path:
        return self.job_dir / REQUEST_FILE

    @property
    def result_path(self) -> Path:
        return self.output_dir / RESULT_FILE

    @property
    def artifacts_dir(self) -> Path:
        return self.output_dir / "artifacts"

    @property
    def work_dir(self) -> Path:
        return self.output_dir / "work"

    @property
    def logs_dir(self) -> Path:
        return self.job_dir / "logs"

    def with_output_root(self, output_root: str) -> "JobLayout":
        return JobLayout(self.job_dir, output_root)

    def resolve(self, rel: str) -> Path:
        """A relative request path → an absolute one, strictly inside the job folder.

        The schema already forbade "..", but a symlink/junction inside input/ can still
        lead outside — so the already resolved path is checked.
        """
        p = (self.job_dir / rel).resolve()
        if not p.is_relative_to(self.job_dir):
            raise WorkerError(ErrorCode.INPUT_PATH_INVALID, args={"path": rel}, details={"path": rel})
        return p

    def relative_to_output(self, p: Path) -> str:
        return p.resolve().relative_to(self.output_dir).as_posix()

    def previous_run_leftovers(self) -> list[str]:
        """Traces of a previous run of the same job.

        Automatic repeat is forbidden: if result.json or artifacts already exist, the
        job has already run, and its fate is for Domain Training to decide, not us.
        Silently overwriting someone else's result is the worst outcome.
        """
        found = []
        if self.result_path.exists():
            found.append(self.relative_to_output(self.result_path))
        if self.artifacts_dir.is_dir() and any(self.artifacts_dir.iterdir()):
            found.append(self.relative_to_output(self.artifacts_dir) + "/")
        return found


def atomic_write_json(path: Path, data: Any) -> None:
    """Write JSON so that a reader sees either the old or the whole new content.

    Write into a temporary file next to it (the same volume — otherwise rename is not atomic),
    flush it to disk and only then rename. If the worker is killed
    at any step, result.json never appears half-written: at worst
    a .tmp remains that nobody reads.
    """
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(f".{path.name}.{os.getpid()}.tmp")
    try:
        with tmp.open("w", encoding="utf-8", newline="\n") as fh:
            json.dump(data, fh, ensure_ascii=False, indent=2)
            fh.write("\n")
            fh.flush()
            os.fsync(fh.fileno())
        os.replace(tmp, path)
    except BaseException:
        tmp.unlink(missing_ok=True)
        raise


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()
