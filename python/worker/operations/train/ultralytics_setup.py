"""Making ultralytics work offline inside the job folder.

Called after runtime.offline.configure_environment and the ultralytics import.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path
from typing import Any

# Integrations that, when their package is present, attach to training by themselves and
# send data outside (or at least try). The worker does not need them.
_INTEGRATIONS_OFF = ("sync", "clearml", "comet", "dvc", "mlflow", "raytune", "tensorboard", "wandb",
                     "vscode_msg", "openvino_msg")

# GPUs on which AMP gives NaN; the same list as in ultralytics.utils.checks.check_amp
_AMP_BAD_GPU = re.compile(
    r"(nvidia|geforce|quadro|tesla).*?(1660|1650|1630|t400|t550|t600|t1000|t1200|t2000|k40m)", re.IGNORECASE
)


def configure(work_dir: Path) -> None:
    from ultralytics.utils import SETTINGS

    SETTINGS.update(
        {
            **{k: False for k in _INTEGRATIONS_OFF if k in SETTINGS},
            "weights_dir": str(work_dir / "weights-cache"),
            "datasets_dir": str(work_dir / "datasets-cache"),
            "runs_dir": str(work_dir / "runs"),
        }
    )
    _replace_amp_check()
    _replace_font_check()


def _offline_check_amp(model: Any) -> bool:
    """A replacement of the ultralytics check_amp without downloading.

    The original compares fp32 and AMP on an unrelated network yolo26n.pt, which it
    downloads from the internet ("not used for training" in its own sources).
    Offline this is impossible, so only the known GPU
    blacklist remains. The check runs only with amp=true in the request (the
    default); with amp=false the trainer does not call it.
    """
    import torch

    device = next(model.parameters()).device
    if device.type != "cuda":
        return False
    gpu = torch.cuda.get_device_name(device)
    if _AMP_BAD_GPU.search(gpu):
        print(f"[amp] {gpu}: NaN with AMP is known — AMP is off", file=sys.stderr, flush=True)
        return False
    print(f"[amp] {gpu}: AMP is on without the reference check (offline)", file=sys.stderr, flush=True)
    return True


def _replace_amp_check() -> None:
    import ultralytics.engine.trainer as trainer_module

    trainer_module.check_amp = _offline_check_amp


def _offline_check_font(font: str = "Arial.ttf") -> str | None:
    """A replacement of the ultralytics check_font without downloading.

    check_det_dataset calls it unconditionally, and Annotator — for labels on
    training images (plots=True), and, not finding the font (the name comparison
    is case-sensitive, and on Windows it is arial.ttf), goes for it to github.com.
    We search only among local fonts, otherwise None: Annotator then takes
    the built-in PIL font (its try/except catches str(None)), the images
    are drawn, the labels are plainer.
    """
    from matplotlib import font_manager

    name = Path(font).name.lower()
    for f in font_manager.fontManager.ttflist:
        if Path(f.fname).name.lower() == name and Path(f.fname).exists():
            return f.fname
    return None


def _replace_font_check() -> None:
    import ultralytics.data.utils as data_utils
    import ultralytics.utils.checks as checks
    import ultralytics.utils.plotting as plotting

    # Every module keeps its own reference (from ... import check_font) —
    # it has to be replaced in all of them, otherwise the training images go to the network.
    data_utils.check_font = _offline_check_font
    checks.check_font = _offline_check_font
    plotting.check_font = _offline_check_font
