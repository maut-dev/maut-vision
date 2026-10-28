"""Раскладка папки задачи и безопасная запись в неё.

training/jobs/<jobId>/
  request.json
  input/{dataset/, model/, manifest.json}   — только чтение
  output/{result.json, artifacts/, work/}   — пишет работник
  logs/                                     — диагностика

output/work/ — черновик ultralytics (results.csv, last.pt, настройки): он
не входит в контракт и не регистрируется. Готовое лежит только в
artifacts/ и перечислено в result.json.
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
        """Относительный путь запроса → абсолютный, строго внутри папки задачи.

        Схема уже запретила «..», но symlink/junction внутри input/ всё ещё
        может увести наружу — поэтому сверяем уже разрешённый путь.
        """
        p = (self.job_dir / rel).resolve()
        if not p.is_relative_to(self.job_dir):
            raise WorkerError(
                ErrorCode.INPUT_PATH_INVALID,
                f"Путь '{rel}' выходит за пределы папки задачи",
                {"path": rel},
            )
        return p

    def relative_to_output(self, p: Path) -> str:
        return p.resolve().relative_to(self.output_dir).as_posix()

    def previous_run_leftovers(self) -> list[str]:
        """Следы прошлого запуска этой же задачи.

        Автоповтор запрещён: если result.json или артефакты уже есть, значит
        задачу уже исполняли, и решать её судьбу — Domain Training, а не нам.
        Перезаписать чужой результат молча — худший исход.
        """
        found = []
        if self.result_path.exists():
            found.append(self.relative_to_output(self.result_path))
        if self.artifacts_dir.is_dir() and any(self.artifacts_dir.iterdir()):
            found.append(self.relative_to_output(self.artifacts_dir) + "/")
        return found


def atomic_write_json(path: Path, data: Any) -> None:
    """Записать JSON так, чтобы читатель видел либо старое, либо целое новое.

    Пишем во временный файл рядом (тот же том — иначе rename не атомарен),
    сбрасываем на диск и только потом переименовываем. Если работника убьют
    на любом шаге, result.json не появится недописанным: в худшем случае
    останется .tmp, который никто не читает.
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
