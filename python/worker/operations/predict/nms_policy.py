"""Use actual tensor geometry for NMS capacity; optional max_det is the user's explicit choice.

The worker runs one operation at a time. Restore the library function even if inference fails.
Pinned Ultralytics 8.4.157 otherwise silently clips at max_nms=30000 and a time budget.
"""
from contextlib import contextmanager
from functools import wraps
from importlib import import_module
from inspect import signature
from typing import Any, Iterator


@contextmanager
def complete_nms(user_limit: int | None, image_size: int) -> Iterator[None]:
    module = import_module("ultralytics.utils.nms")
    original = module.non_max_suppression
    parameters = signature(original)

    @wraps(original)
    def process(prediction: Any, *args: Any, **kwargs: Any) -> Any:
        bound = parameters.bind(prediction, *args, **kwargs)
        bound.apply_defaults()
        tensor = prediction[0] if isinstance(prediction, (list, tuple)) else prediction
        end_to_end = tensor.shape[-1] == 6 or bound.arguments.get("end2end", False)
        candidates = int(tensor.shape[1] if end_to_end else tensor.shape[-1])
        classes = int(bound.arguments.get("nc", 0) or max(1, tensor.shape[1] - 4))
        if bound.arguments.get("multi_label", False) and not end_to_end:
            candidates *= classes
        bound.arguments["max_det"] = user_limit if user_limit is not None else candidates
        bound.arguments["max_nms"] = max(1, candidates)
        bound.arguments["max_time_img"] = float("inf")
        # Class offsets must exceed the actual coordinate span, including large image sizes.
        coordinates = tensor[..., :4] if end_to_end else tensor[:, :4, :]
        extent = float(coordinates.abs().max().item()) if coordinates.numel() else float(image_size)
        bound.arguments["max_wh"] = max(float(image_size) * 4 + 1, extent * 4 + 1)
        return original(*bound.args, **bound.kwargs)

    module.non_max_suppression = process
    try:
        yield
    finally:
        module.non_max_suppression = original
