from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any

from worker.protocol import ResultWarning
from worker.protocol.messages import render
from worker.runtime.events import EventEmitter
from worker.runtime.job_layout import JobLayout


@dataclass
class OperationContext:
    layout: JobLayout
    events: EventEmitter
    warnings: list[ResultWarning] = field(default_factory=list)

    def warn(self, code: str, reason: str | None = None, args: dict[str, Any] | None = None) -> None:
        """A warning goes both as an event (seen at once) and into result.json; its text comes from worker/protocol/messages.py."""
        message, values = render(code, reason, args)
        self.warnings.append(ResultWarning(code=code, message=message, reason=reason, args=values))
        fields: dict[str, Any] = {"code": code, "message": message}
        if reason is not None:
            fields["reason"] = reason
        if values:
            fields["args"] = values
        self.events.emit("warning", **fields)

    def stage(self, name: str) -> None:
        self.events.emit("stage", stage=name)
