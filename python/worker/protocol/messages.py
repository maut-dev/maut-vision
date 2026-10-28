"""English texts of worker errors and warnings, keyed by ``<code>`` or ``<code>.<reason>``.

A failure or a warning carries its code, an optional reason and ``args`` (strings) in result.json and in ``warning`` events; the message is
the text of this table with the args substituted. The message goes to stderr and the job log, and the UI shows it only for a key its
catalogs do not know: the UI translates the key ``worker:<code>.<reason>`` (or ``worker:<code>``) with the same args. A code either has
reasons for all its messages or none, so a key is never both a text and a group. Keys and ``{placeholders}`` must match
``ui/src/locales/<language>/worker.json`` (checked by ``npm run check-locales``).
"""
from __future__ import annotations

from collections.abc import Mapping

MESSAGES: dict[str, str] = {
    # request
    "request_not_found": "The job folder has no request.json",
    "request_unreadable": "request.json cannot be read as JSON: {error}",
    "request_invalid.not_object": "request.json must be a JSON object",
    "request_invalid.validation": "The request did not pass validation: {summary}",
    "request_invalid.class_out_of_range": "The classes filter has an index outside the dataset class map",
    "request_invalid.compile_unavailable": "torch.compile is unavailable in the pinned Windows build without Triton",
    "request_invalid.profile_unavailable": "Profiling requires TensorRT/ONNXRuntime and disabled external integrations",
    "protocol_unsupported": "Protocol version {received} is not supported, the worker understands {supported}",
    "operation_not_implemented": "The operation '{operation}' is in the protocol, but this version of the worker does not implement it",
    # input files
    "input_path_invalid": "The path '{path}' goes outside the job folder",
    "dataset_invalid.yaml_missing": "input/dataset has no data.yaml",
    "dataset_invalid.yaml_unreadable": "data.yaml cannot be read: {error}",
    "dataset_invalid.yaml_not_mapping": "data.yaml must be a mapping",
    "dataset_invalid.names_invalid": "data.yaml: 'names' must be a non-empty list of class names",
    "dataset_invalid.nc_mismatch": "data.yaml: nc={nc}, but there are {names} class names",
    "dataset_invalid.key_missing": "data.yaml: the key '{key}' is missing",
    "dataset_invalid.split_path_type": "data.yaml: '{key}' must be a path or a list of paths",
    "dataset_invalid.split_outside": "data.yaml: '{key}' points outside the dataset",
    "dataset_invalid.split_missing": "The dataset has no '{value}' (key '{key}' in data.yaml)",
    "dataset_invalid.split_empty": "The split '{key}' has no images",
    "dataset_invalid.validation_split_empty": "The chosen validation split '{key}' has no images",
    "dataset_invalid.test_split_missing": "The dataset has no test split",
    "model_not_found": "There is no weights file '{file}' in input/model",
    "model_load_failed.not_pt": "Weights must be a .pt file, got '{file}'",
    "model_load_failed.load_error": "The weights '{file}' could not be loaded: {error}",
    "architecture_mismatch.base_weights": (
        "Base weights: {claimed} is declared, but the weights hold '{yamlFile}' (scale '{scale}', task '{task}'). Training did not start."
    ),
    "architecture_mismatch.best_pt": (
        "best.pt: {claimed} is declared, but the weights hold '{yamlFile}' (scale '{scale}', task '{task}'). Training did not start."
    ),
    "architecture_mismatch.version_weights": (
        "Version weights: {claimed} is declared, but the weights hold '{yamlFile}' (scale '{scale}', task '{task}'). Training did not start."
    ),
    "device_unavailable.no_cuda": "GPU {index} is requested, but CUDA is unavailable",
    "device_unavailable.gpu_missing": "GPU {index} is requested, but {count} are visible",
    # execution
    "out_of_memory.train": "The device ran out of memory: reduce batch or imgsz. {error}",
    "out_of_memory.evaluate": "The device ran out of memory: reduce batch. {error}",
    "out_of_memory.predict": "The device ran out of memory. {error}",
    "training_failed": "Training failed: {error}",
    "evaluation_failed": "The evaluation failed: {error}",
    "no_weights_produced": "Training did not save best.pt: no epoch was completed",
    # checks: predict, live, screenshot
    "source_invalid.missing": "There is no source file of the check",
    "source_invalid.image_unreadable": "The image could not be read: {error}",
    "source_invalid.image_format": "The file is not recognized as an image (PNG, JPEG, WebP)",
    "source_invalid.video_unopened": "The video did not open: OpenCV does not support the format or the file is damaged",
    "source_invalid.video_empty": "Not a single frame was read from the video",
    "region_invalid.outside_image": "The region {width}×{height} at ({x}, {y}) goes beyond the image {frameWidth}×{frameHeight}",
    "region_invalid.outside_video_frame": "The region {width}×{height} at ({x}, {y}) goes beyond the video frame {frameWidth}×{frameHeight}",
    "region_invalid.outside_monitor": "The region {width}×{height} at ({x}, {y}) goes beyond the monitor {frameWidth}×{frameHeight}",
    "prediction_failed": "The model run failed: {error}",
    "capture_unavailable.mss_missing": "Screen capture is unavailable: mss is not installed ({error})",
    "capture_unavailable.open_failed": "Screen capture could not be opened: {error}",
    "capture_unavailable.monitor_missing": "Monitor {monitor} does not exist: 0..{last} are available",
    "capture_unavailable.monitor_changed": (
        "Monitor {monitor} changed after the screenshot: it is now {actualWidth}×{actualHeight} at ({actualX}, {actualY}), but the region was "
        "chosen on {width}×{height} at ({x}, {y}). Take the screenshot again."
    ),
    "internal_error.png_encode": "The screenshot could not be encoded to PNG",
    "internal_error.unexpected": "Internal error of the worker: {error}",
    # warnings
    "live_time_limit": "The screen check stopped at its limit of {seconds} s",
    "video_fps_unknown": "The video does not report its frame rate: frame times are computed for 30 fps",
    "reports_failed": "Training plots and log were not saved: {error}",
    "onnx_export_failed.export_error": "ONNX was not exported: {error}",
    "onnx_export_failed.no_file": "The ONNX export finished without a file",
    "batch_reduced": "Ultralytics reduced the batch from {requested} to {actual} for lack of memory; lr0 {lr0} was computed for the original batch",
    "amp_disabled": "AMP is off: mixed precision gives NaN on this GPU, training ran in fp32",
}


def message_key(code: str, reason: str | None) -> str:
    return code if reason is None else f"{code}.{reason}"


def render(code: str, reason: str | None = None, args: Mapping[str, object] | None = None) -> tuple[str, dict[str, str]]:
    """The English message of the key and the args as strings (what result.json and the events carry)."""
    values = {name: str(value) for name, value in (args or {}).items()}
    return MESSAGES[message_key(code, reason)].format(**values), values
