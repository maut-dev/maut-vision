"""Training operation; importing protocol/progress helpers does not load ML dependencies."""
from typing import Any


def run_train(request: Any, ctx: Any) -> dict[str, Any]:
    from worker.operations.train.run import run_train as implementation

    return implementation(request, ctx)


__all__ = ["run_train"]
