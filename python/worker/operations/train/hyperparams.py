"""Learning rate, warmup and the number of data loading processes (with the reasons for the choice)."""
from __future__ import annotations

import os

# MEASURED (a dataset with 54 classes): at batch 32 lr0=0.001 trains
# the classifier. With optimizer="auto" ultralytics itself took AdamW with
# lr = 0.002*5/(4+nc) = 0.00017 — six times lower than a working one: box_loss fell
# from 1.74 to 0.27, while the classification head did not move and gave 0.004 even
# on training images. This is the only measured point, the rest
# is derived from it.
LR_REF = 0.001
LR_REF_BATCH = 32

# The warmup cap is a share of the whole run: on a short run multiplying by
# batch/32 would give the whole training to the warmup.
WARMUP_MAX_SHARE = 0.25


def recommended_lr0(batch: int) -> float:
    """LR_REF * batch / LR_REF_BATCH.

    Per epoch the network makes images/batch steps. Batch 256 instead of 32 means eight
    times fewer steps, and with the same lr the first epoch looks like a failure.
    The linear rule restores the same total weight shift per epoch.
    Rounding to 6 digits — otherwise 0.0030000000000000005 ends up in the passport.
    """
    b = max(1, int(batch))
    return round(LR_REF * b / LR_REF_BATCH, 6)


def warmup_epochs_for(epochs: int, batch: int) -> float:
    """Warmup in EPOCHS chosen so that in STEPS it does not depend on the batch.

    Ultralytics measures warmup in epochs, and an epoch has images/batch steps:
    "3 epochs" with a large batch are a hundred steps, and lr jumps to the working value
    before a fresh classification head comes alive. The base is longer than
    the standard 3 epochs on long runs: on poor classes a short
    warmup drove the classifier to zeros. The cap is WARMUP_MAX_SHARE
    of the run, but not less than one epoch.
    """
    epochs = max(1, int(epochs))
    base = max(1.0, min(5.0, epochs / 10))
    scaled = base * max(1, int(batch)) / LR_REF_BATCH
    return round(min(scaled, max(1.0, epochs * WARMUP_MAX_SHARE)), 2)


def default_patience(epochs: int) -> int:
    """Early stopping: no earlier than 10 epochs without improvement and no later than 40."""
    return max(10, min(40, int(epochs)))


def dataloader_workers() -> int:
    """Number of data loading processes.

    MEASURED: on Windows child processes are created via
    spawn, and torch sends them the whole dataset — together with the image cache
    in memory. On 1500 frames that is half a gigabyte in the pipe and a crash
    "OSError: [Errno 22] Invalid argument". So on Windows loading
    runs in the main process (workers=0).
    """
    if os.name == "nt":
        return 0
    return min(4, os.cpu_count() or 1)
