# Python worker

The ML executor of Maut Vision. The worker performs one operation on a job folder and exits. It knows nothing about SQLite, the host or
Core areas: its contract is only the job files and JSON Lines on stdout. Process rules are in
[docs/Python/worker.md](../docs/Python/worker.md), the host side in [docs/Core/training.md](../docs/Core/training.md).

## Running

```text
maut-training-worker.exe <jobDir>        # the built onedir
python -m worker <jobDir>                # from sources: PYTHONPATH = python/, cwd = the job folder
```

```text
<jobDir>/
  request.json
  input/{dataset/, model/<weights>.pt, manifest.json}   read-only
  output/result.json                                    published atomically, last
  output/artifacts/{best.pt, best.onnx}                 what result.json lists
  output/artifacts/{plots/*.png|jpg, training.log}      report files (result.reports)
  output/work/                                          ultralytics scratch, not a contract
  logs/worker.log                                       a copy of stderr, UTF-8
```

## Structure

| Module | Role |
|---|---|
| `worker/main.py` | entry point, exit codes, building and publishing result.json |
| `worker/protocol/` | Pydantic request/result schemas, error codes, `PROTOCOL_VERSION` |
| `worker/runtime/` | job folder layout and atomic writes, stdout events, offline mode |
| `worker/operations/` | operation dispatcher: `train/`, `evaluate/`, `predict/`, `live/`, `screenshot/`, screen capture `capture.py` |

## request.json (protocolVersion 1)

```json
{
  "protocolVersion": 1,
  "operationId": "run-42",
  "operation": "train",
  "input": { "dataset": "input/dataset", "model": "input/model/yolo26n.pt", "manifest": "input/manifest.json" },
  "outputRoot": "output",
  "params": {
    "architecture": "yolo26n", "epochs": 120, "batch": 32, "imgsz": 640, "device": "0",
    "seed": 0, "lr0": null, "warmupEpochs": null, "amp": true, "exportOnnx": true,
    "mosaic": 0.0, "scale": 0.1, "translate": 0.05
  }
}
```

Required: `architecture`, `epochs`, `batch`, `imgsz` (a multiple of 32), `device` (`cpu`, a GPU index or `-1` for automatic GPU
selection). Everything else has defaults — the full list is in `worker/protocol/request.py`. Extra fields are forbidden. Paths are
relative, inside the job folder; inputs only in `input/`. Older requests with nested `augmentations` stay readable; explicit flat
parameters take priority.

`operation: "evaluate"` checks the version weights on a dataset (`model.val`): `result` has `metrics`,
`classMetrics[{index,name,metrics}]` and `effectiveParams`. `operation: "export"` returns `operation_not_implemented`.

### Checks: predict, live, screenshot

Each operation declares its own `input`.

- `predict` — `input {model, source, manifest?}` (the source in `input/source/`), `params {architecture, imgsz, device, conf (0..1, 0.25),
  source: image|video, stepMs (1000), region {x,y,width,height}|null, maxDetections (optional user limit), previewIntervalMs (1000)}`. An
  image is one `model.predict` with a `frame` event always (with a preview) and `frames[1]` in the result. A video is read with
  `cv2.VideoCapture` by a relative ASCII path from the job folder (VideoCapture on Windows cannot open non-ANSI paths), **sequentially**:
  skipped frames are only `grab()`bed without decoding and there is no seeking (seeking is an order of magnitude slower); the step in frames
  is round(stepMs·fps/1000). `frame` is sent for every analysed frame with detections, `progress` at most once per second. A region outside
  the frame is `region_invalid`, an unreadable file `source_invalid`.
- `live` — `input {model, manifest?}`, `params {architecture, imgsz, device, conf, monitor (1), region|null, fps (finite > 0, default 3),
  maxDurationSec (1800), maxDetections, previewIntervalMs}`. mss captures the monitor region → predict → `frame` for every frame (empty
  ones too), then a pause until the fps period. A normal stop comes from outside (Core ends the process tree, no result.json); at
  maxDurationSec the worker exits by itself with a summary and the warning `live_time_limit`. A closed event pipe stops it on the next
  frame.
- `screenshot` — no `input`, `params {monitor}`; only mss and OpenCV (no torch/ultralytics). PNG in `output/artifacts/screen.png`, in the
  result `screenshot {path, sizeBytes, sha256, width, height, monitor, monitors[{index,left,top,width,height}]}`. No mss or no screen —
  `capture_unavailable`.

A `frame` event: `index` (the video frame number; for the screen a sequence number), `timeMs`, `width`/`height` of the analysed frame (the
region), `detections[{classIndex, name, confidence, box{x,y,width,height}}]` — the box in frame fractions, `inferenceMs` and an optional
`previewFile` — a JPEG in `output/previews/`, at most every `previewIntervalMs`. The predict/live result: `summary {framesProcessed,
framesWithDetections, detections, classes[{index,name,frames,detections,maxConfidence}]}`, `source {width,height,durationMs?,fps?}`,
`frames`. The training history comes in `result.history` per epoch; an epoch record has `fitness` (what ultralytics uses to choose
best.pt — mAP50-95).

## Design decisions

- **The architecture is checked by the weights**, not by the file name: `yaml_file` and `scale` from the checkpoint plus `task`. Before
  training — on the source weights, after — on `best.pt`. A foreign one is `architecture_mismatch`, and training does not start.
- **Default lr0 = 0.001 · batch / 32.** The automatic learning rate of ultralytics was measured to be too low for datasets with many
  classes, and the classifier did not learn.
- **Default warm-up** = max(1, min(5, epochs/10)) · batch/32, at most a quarter of the run and at least one epoch. Both actual values and
  their source (`request`/`computed`) are in `effectiveParams`.
- **AMP is on by default**, as in ultralytics; the AMP check is offline (see "Offline"). On the CPU and on blacklisted GPUs the trainer
  turns it off: `effectiveParams.amp` is the actual value, `ampRequested` the requested one; on a GPU switching it off gives the warning
  `amp_disabled`.
- **The user chooses workers.** Requests without `workers` keep the fallback: Windows — 0, other platforms — min(4, CPU).
- **UTF-8 everywhere**: stderr and `logs/worker.log` — ultralytics prints emoji, which a legacy code page cannot encode.
- **A failed ONNX export is a warning** `onnx_export_failed`, not a failure: `best.pt` is already done.

## Detection training and the live passport

Pinned runtime: see `requirements.txt` (Ultralytics, Torch with CUDA 13). The full argument registry is owned by Core training; requests
pass allowed camelCase values, and `operations/train/options.py` turns them into snake_case for `model.train`. Sources of the registry: the
installed `ultralytics/cfg/default.yaml` and the official [train](https://docs.ultralytics.com/modes/train) and
[cfg](https://docs.ultralytics.com/usage/cfg) pages.

- Batch: a positive integer, `-1` (AutoBatch) or a share of GPU memory `(0,1)`. The requested batch is kept separately from the actual
  positive `effectiveBatch`.
- Optimizer: auto, SGD, MuSGD, Adam, Adamax, AdamW, NAdam, RAdam, RMSProp. After `on_train_start` the passport gets the real optimizer
  class, the initial LR of the groups, the actual AMP precision, the device, workers and the model output map. With `auto` the actual lr0 is
  marked `optimizer-auto` and not presented as a user LR.
- `pretrained=false` is passed to the standard `Model.train`: the pinned runtime calls `get_model(weights=None, cfg=...)`, i.e. it really
  builds random weights.
- `singleCls=true` gets the actual map `{0: item}` from the trainer. The `classes` filter keeps the original output indexes; indexes are
  checked against the immutable dataset. The selected `split` really chooses train/val/test for the validation loader through a working
  YAML; the input YAML stays unchanged, and an empty selected split is rejected.
- `save=false` turns off intermediate checkpoints, `val=false` intermediate validation. Ultralytics still validates and saves the final
  epoch. This is reflected in `intermediateValidation`, `intermediateCheckpoints`, `finalValidation`, `finalCheckpoint`. Without best.pt
  the validated last.pt is used; the choice is in `checkpointSelection`.
- `compile=true` and `profile=true` are rejected with a concrete reason: the Windows runtime has no Triton/TensorRT/ONNX Runtime, and
  external logger integrations are off. Resume is not exposed: the published best.pt no longer contains the optimizer state. Arbitrary
  cfg/teacher paths and shell commands are not accepted.

Protocol v1 events: `environment {environment}` (python, torch, ultralytics, cuda, deviceName — before training; the host keeps it also
for an interrupted run), `effectiveParams {parameters,capture:requested|trainer}`, `log {message}` and fitness in `epoch`. The first
snapshot comes before training, the second after AutoBatch/optimizer initialization. The console log arrives as real lines in chunks of up
to 2,000 characters with machine paths removed; the full local file is kept separately. Each epoch is saved with fsync before its event is
sent. With `val=false` an epoch without validation sends empty metrics and fitness=null instead of stale numbers.

## Offline

The worker downloads nothing. `YOLO_OFFLINE`, `YOLO_AUTOINSTALL=false` and ultralytics settings in `output/work/ultralytics-config`.
Ultralytics checks that go to the network are replaced locally: `check_amp` (downloads yolo26n.pt) by a GPU blacklist, `check_font`
(downloads Arial) by a search among system fonts. The last line of defence forbids outgoing socket connections except loopback; an attempt
leaves an `[offline]` line with a stack trace in stderr. Base weights are only an existing `.pt`: a name without a file or a `.yaml` is not
accepted.

## stdout events

One JSON line per event: `type`, a monotonic `sequence` from 1, `ts`. Everything else ultralytics/torch print goes to stderr: fd 1 is
redirected before the libraries are imported.

```json
{"type":"started","sequence":1,"ts":"…","operation":"train","operationId":"run-42","protocolVersion":1,"pid":18364}
{"type":"stage","sequence":2,"ts":"…","stage":"validating"}
{"type":"batch","sequence":6,"ts":"…","epoch":1,"batch":3,"batches":3}
{"type":"epoch","sequence":9,"ts":"…","epoch":2,"epochs":2,"losses":{"box_loss":1.44,"cls_loss":7.79,"dfl_loss":1.08},"metrics":{"precision":0.011,"recall":1.0,"mAP50":0.048,"mAP50_95":0.028},"lr":0.000126}
{"type":"warning","sequence":10,"ts":"…","code":"batch_reduced","args":{"requested":"32","actual":"16","lr0":"0.001"},"message":"…"}
{"type":"frame","sequence":7,"ts":"…","index":30,"timeMs":3000,"width":256,"height":256,"detections":[{"classIndex":1,"name":"mug","confidence":0.87,"box":{"x":0.1,"y":0.2,"width":0.3,"height":0.4}}],"inferenceMs":9,"previewFile":"previews/frame-30.jpg"}
{"type":"progress","sequence":8,"ts":"…","framesProcessed":4,"framesTotal":120,"positionMs":3000,"durationMs":120000}
{"type":"finished","sequence":12,"ts":"…","status":"succeeded","errorCode":null}
```

Stages: `validating`, `loading_model`, `training`, `evaluating`, `predicting`, `capturing`, `finalizing`, `exporting`. `batch` at most
once per second. The final validation of best.pt is not reported as an extra epoch.

## result.json

`status` `succeeded|failed`, `error {code, message, details, reason, args}`, `warnings [{code, message, reason, args}]`, `architecture` (what the weights contain), `effectiveParams`,
`metrics` (validation of best.pt), `epochsCompleted`, `stoppedEarly`, `historyFile` (history.jsonl: path, size, checksum, count),
`artifacts [{kind, path, sizeBytes, sha256}]` (paths relative to outputRoot), optional `reports [{kind: plot|log, path, sizeBytes,
sha256}]`, `environment`. An epoch of the history: `losses` — train (box_loss, cls_loss, dfl_loss), `metrics` — quality and validation
losses (valBoxLoss, valClsLoss, valDflLoss), `lr`, `fitness`.

**Report files.** Training runs with `plots=True`: afterwards pictures from the ultralytics run folder (results.png, confusion_matrix*.png,
labels.jpg, train_batch*.jpg, val_batch*.jpg, *curve.png) are copied to `output/artifacts/plots/`. The training console output is
duplicated during `model.train` to `output/work/console.raw.log`, then cleaned — progress bars keep their last state, ANSI is removed — and
streamed to `output/artifacts/training.log`. The log never goes into the stdout protocol. A failure to collect reports is the warning
`reports_failed`, not a failure. The font for plot labels is looked up only locally. For a request that failed validation `operationId` is
taken from the raw JSON if it looks like an identifier, otherwise `null`.

Error codes: `worker/protocol/errors.py`. Messages: `worker/protocol/messages.py` — English texts keyed by `<code>` or `<code>.<reason>`;
`reason` and `args` (strings) are an optional extension of protocol 1 that lets the UI show the message in its language
(`ui/src/locales/<language>/worker.json`, checked by `npm run check-locales`). Errors are raised as
`WorkerError(ErrorCode.X, "reason", {"arg": value}, details)`, warnings as `ctx.warn("code", "reason", {"arg": value})`; see
[docs/Python/worker.md](../docs/Python/worker.md#errors-and-warnings).

## Exit codes

| Code | Meaning |
|---|---|
| 0 | succeeded, result.json published |
| 1 | failed, result.json with the error published |
| 2 | no job folder in the arguments |
| 3 | result.json could not be written |
| 4 | the job already has result.json or artifacts — automatic retry is forbidden, nothing touched |
| 130 | interrupted by Ctrl+C/Ctrl+Break or the event pipe closed (the application died), no result |

The exit code is secondary: success is only a valid result.json with `status: succeeded`. A killed process never leaves result.json: it is
written to a temporary file and renamed. Unfinished `output/work/` and possibly `.result.json.<pid>.tmp` stay — Core decides what happens
to the folder.

## Development and checks

```powershell
.\tools\setup-python.ps1                 # from the repository root: .dev/python-venv
cd python
..\.dev\python-venv\Scripts\python.exe -I -m unittest discover -s tests -t .
..\.dev\python-venv\Scripts\python.exe tools/make_fixture_job.py ..\.dev\jobs\valid --weights C:\path\yolo26n.pt --epochs 2
$env:PYTHONPATH = (Get-Location).Path; ..\.dev\python-venv\Scripts\python.exe -m worker ..\.dev\jobs\valid
```

The development environment is a venv with Python 3.14 and the versions from `requirements.txt`; `tools/setup-python.ps1` creates it. The
host runs it as `python.exe -m worker .` with PYTHONPATH = `python/` (`start.ps1` picks `.dev/python-venv` automatically, or
`-Python <python.exe>`). The host side is checked by smoke with a fake worker (`tests/MautVision.FakeWorker`), real training by
`tools/run-e2e.ps1`.

Building the onedir (needs PyInstaller from `requirements-build.txt` in the chosen interpreter; the script installs nothing without
`-InstallBuildDependencies`):

```powershell
.\python\scripts\build-worker.ps1 -Python .dev\python-venv\Scripts\python.exe
```

The result is `.dev/python-worker/maut-training-worker/maut-training-worker.exe` and the `_internal` folder (≈ 3.4 GB, ≈ 4,800 files)
with `BUILD.txt` holding the commit and the source hash next to it ([worker.md](../docs/Python/worker.md#build)). In the delivery the whole
folder lies in `python/maut-training-worker/` of the application directory. The model catalog is copied into the build as a fallback;
normally the host passes its path.

`worker_entry.py` calls `multiprocessing.freeze_support()` before importing the protocol: this is required for a DataLoader with
`workers>0` in a frozen Windows executable ([PyInstaller rule](https://pyinstaller.org/en/stable/common-issues-and-pitfalls.html#multi-processing)).
The spec includes the Ultralytics cfg/assets and the lazy imports of ONNX/onnxslim/mss and excludes onnxruntime (ONNX export works
without it); the torchvision DLLs `_C_stable`/`image_stable` are added explicitly because the stock hook looks for `torchvision._C`.

## Limitations

- `export` is not implemented.
- Cancellation happens from outside by ending the process tree; there is no soft stop protocol.
- A closed event pipe means the application died: Core does not pick such a run up again, so the worker exits immediately (code 130,
  without result.json) instead of training in vain.
- 59 detection models from `catalog/yolo-catalog.json` are supported (the host passes the path in `MAUT_MODEL_CATALOG`): YOLOv3u, YOLOv5u
  (P6 included), YOLOv8, YOLOv9, YOLOv10, YOLO11, YOLO12 and YOLO26. The architecture check uses the family, scale and configuration of
  the checkpoint; the image size must be divisible by the model stride (32 or 64). Weights are installed by the user in the application;
  the worker works without a network.
