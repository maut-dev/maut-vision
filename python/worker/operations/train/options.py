"""Pinned detect train options and truthful requested/effective snapshots."""
from __future__ import annotations

from typing import Any

from worker.operations.train import hyperparams
from worker.protocol import TrainParams


def resolved_options(params: TrainParams, workers: int) -> tuple[dict[str, Any], dict[str, Any]]:
    requested = params.model_dump(by_alias=True, exclude={"augmentations"})
    options = params.model_dump(exclude={"architecture", "augmentations", "export_onnx"})
    # Protocol v1 legacy jobs carried augmentations in a nested object.
    for key, value in params.augmentations.model_dump().items():
        if key not in params.model_fields_set:
            options[key] = value
    # AutoBatch's memory fraction is not a batch count. Resolve automatic LR against
    # the nominal default, then let optimizer=auto select its own actual LR.
    reference_batch = int(params.batch) if params.batch >= 1 else 16
    options["lr0"] = params.lr0 if params.lr0 is not None else hyperparams.recommended_lr0(reference_batch)
    options["warmup_epochs"] = params.warmup_epochs if params.warmup_epochs is not None else hyperparams.warmup_epochs_for(params.epochs, reference_batch)
    options["patience"] = params.patience if params.patience is not None else hyperparams.default_patience(params.epochs)
    options["workers"] = workers
    effective = dict(requested)
    effective.update({
        "lr0": options["lr0"],
        "lr0Source": "request" if params.lr0 is not None else "computed",
        "warmupEpochs": options["warmup_epochs"],
        "warmupEpochsSource": "request" if params.warmup_epochs is not None else "computed",
        "patience": options["patience"],
        "workers": workers,
        "workersRequested": params.workers,
        "channelsLastRequested": params.channels_last,
        "ampRequested": params.amp,
        "optimizerRequested": params.optimizer,
        "augmentations": {key: options[key] for key in params.augmentations.model_dump()} | {"cutmix": options["cutmix"], "bgr": options["bgr"]},
        "intermediateValidation": params.val,
        "intermediateCheckpoints": params.save,
        "finalValidation": True,
        "finalCheckpoint": True,
    })
    return options, effective


def trainer_snapshot(trainer: Any, effective: dict[str, Any]) -> dict[str, Any]:
    """Read real values after the optimizer and AutoBatch have been initialized."""
    snapshot = dict(effective)
    args = getattr(trainer, "args", None)
    snapshot["effectiveBatch"] = int(getattr(trainer, "batch_size", 0) or getattr(args, "batch", 1))
    snapshot["amp"] = bool(getattr(trainer, "amp", False))
    snapshot["precision"] = "bfloat16" if snapshot["amp"] and getattr(args, "amp", None) == "bf16" else "float16" if snapshot["amp"] else "float32"
    snapshot["device"] = str(getattr(trainer, "device", snapshot["device"]))
    snapshot["imgsz"] = int(getattr(args, "imgsz", snapshot["imgsz"]))
    snapshot["workers"] = int(getattr(getattr(trainer, "train_loader", None), "num_workers", snapshot["workers"]))
    optimizer = getattr(trainer, "optimizer", None)
    if optimizer is not None:
        snapshot["optimizer"] = type(optimizer).__name__
        groups = getattr(optimizer, "param_groups", [])
        snapshot["optimizerGroups"] = [
            {"lr": float(group["lr"]), "initialLr": float(group.get("initial_lr", group["lr"])), "weightDecay": float(group.get("weight_decay", 0))}
            for group in groups
        ]
        if groups:
            snapshot["lr0"] = float(groups[0].get("initial_lr", groups[0]["lr"]))
            if snapshot["optimizerRequested"] == "auto":
                snapshot["lr0Source"] = "optimizer-auto"
                snapshot["momentum"] = float(groups[0].get("momentum", groups[0].get("betas", (0.9,))[0]))
        snapshot["warmupBiasLr"] = float(getattr(args, "warmup_bias_lr", snapshot["warmupBiasLr"]))
    names = getattr(getattr(trainer, "model", None), "names", None)
    if names is None:
        names = (getattr(trainer, "data", None) or {}).get("names")
    if isinstance(names, (dict, list, tuple)):
        pairs = sorted(names.items()) if isinstance(names, dict) else enumerate(names)
        snapshot["outputClasses"] = [{"index": int(index), "name": str(name)} for index, name in pairs]
    return snapshot
