from __future__ import annotations

from dataclasses import dataclass, field

from worker.protocol import ResultWarning
from worker.runtime.events import EventEmitter
from worker.runtime.job_layout import JobLayout


@dataclass
class OperationContext:
    layout: JobLayout
    events: EventEmitter
    warnings: list[ResultWarning] = field(default_factory=list)

    def warn(self, code: str, message: str) -> None:
        """Предупреждение идёт и событием (видно сразу), и в result.json."""
        self.warnings.append(ResultWarning(code=code, message=message))
        self.events.emit("warning", code=code, message=message)

    def stage(self, name: str) -> None:
        self.events.emit("stage", stage=name)
