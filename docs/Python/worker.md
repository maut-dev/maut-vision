# Python worker

Python runs ML operations: training, evaluation on a dataset, prediction on an image, a video or the screen. One worker process performs
a whole check: the model is loaded once. Screenshots and the monitor list are taken by C#, without a Python process. Everything else —
projects, the queue, statuses, storage, retry decisions — belongs to C#. Python never becomes a general backend and never starts an HTTP
server.

```text
Core/Jobs → job handler → Python executor (Core) → worker process
                               ├─ job folder: request.json, input/ → output/result.json
                               ├─ stdout: JSON Lines events
                               └─ stderr: job log
```

The worker's own layout, request fields, events and exit codes are described in [python/README.md](../../python/README.md).

## Process

One process performs one operation on its job folder and exits. Core starts the executable with an argument array, without a shell and
without searching for Python in PATH. The working directory of the process is the job folder; paths in the request are relative and never
leave it. This way the worker does not depend on non-ASCII characters in the user profile: OpenCV and some native libraries on Windows
cannot open such paths.

The worker process tree belongs to Core and never outlives the application, also when the host crashes. The process is placed into a
Windows Job Object with kill-on-close; an explicit stop ends the whole tree, not only the parent PID. A new attempt of the same job does
not start until the stop of the previous one is confirmed.

Protocol v1 has no cooperative stop signal: cancellation ends the process tree through the Job Object, and a worker whose event pipe
closed exits by itself (code 130). A screen check stopped this way publishes no result.json; only its time limit writes a summary.

## Launch

The host passes the launch description in `CoreOptions.PythonWorker` (`PythonWorkerLaunch`); Core only checks that the files exist and
reports `training.runtime_not_configured` / `runtime_missing` when an operation starts, not at application start.

```csharp
PythonWorkerLaunch.Packaged(@"<app>\python\maut-training-worker\maut-training-worker.exe");   // delivery
PythonWorkerLaunch.Interpreter(@"<repo>\.dev\python-venv\Scripts\python.exe", @"<repo>\python"); // development: -m worker
```

Desktop chooses the description in this order: `--worker-executable <exe>`, then `--python <python.exe> --python-source <python/ folder>`,
then the built worker in the application directory; otherwise the environment is not configured. `start.ps1` passes `-Python <python.exe>`
or the `MAUT_PYTHON` variable, otherwise the development venv `.dev/python-venv` created by `tools/setup-python.ps1`, otherwise a build from
`.dev/python-worker` if it exists. The process argument is `.` (the job folder is its working folder); the environment is
`PYTHONUTF8=1`, `PYTHONIOENCODING=utf-8`, `PYTHONUNBUFFERED=1`, `PYTHONPATH` — the sources or nothing, `MAUT_MODEL_CATALOG` — the Core
model catalog.

An event line is limited by type: an ordinary event — 64 KiB, a frame (`frame`, the list of detections) — 2 MiB, effectiveParams with the
full class map — 16 MiB. Events are copied to `logs/events.jsonl` (large lines by size only), stderr to `logs/stderr.log`; the worker keeps
`logs/worker.log` itself.

## Protocol

C# materializes the inputs before launch; they are immutable while the worker runs. Large inputs are not copied: the dataset, the version
weights and the check file are hard links in `input/`. The only exception to "paths stay inside the job folder" is `input.externalSource`
of the `predict` operation: the absolute path of the selected file when it is on another volume and a link is impossible; the file is only
read. Python validates the request again and rejects extra fields. The protocol version is checked by both sides: the UI and the host ship
together, but the worker is built and updated separately.

stdout carries only the protocol: one JSON line per event with a type and a monotonic `sequence`. Library output is redirected to stderr
before the libraries are imported. C# limits the line length, checks the event type and order, and never passes stdout to the UI
directly — events become Core events. Preview images do not travel in events: the worker writes JPEG files to `output/previews/` (through a
temporary file and a rename) and names them in the `previewFile` field of the `frame` event; C# reads and deletes the file, and the UI gets
the preview through the resource channel.

```text
{"type":"frame","sequence":42,"index":120,"timeMs":4000,"width":640,"height":640,"inferenceMs":9,
 "detections":[{"classIndex":0,"name":"mug","confidence":0.87,"box":{"x":0.41,"y":0.2,"width":0.05,"height":0.12}}],
 "previewFile":"previews/frame-120.jpg"}
```

Operations without a natural end get explicit limits: `live` — `maxDurationSec` (at the limit the worker publishes the summary itself with
the warning `live_time_limit`) and `expectedMonitor` — the monitor bounds of the screenshot on which the region was chosen; if capture sees
the monitor differently, it is the refusal `capture_unavailable`.

The result is defined by result.json, not by the exit code. It is published atomically and last, after all artifacts. Exit code 0 without a
valid result.json is an error; artifacts are accepted only after checking paths, size and SHA-256. Partial output is never registered as a
ready model.

```text
<job folder>/
    request.json        protocol, operation, operationId, inputs, parameters
    input/              read-only for the worker
    output/result.json  status, error {code, message, details}, artifacts
    output/artifacts/
    logs/               worker stderr
```

## Offline and environment

The worker downloads nothing: weights and resources are present before launch, automatic package installation and network checks of
libraries are disabled. Base weights are passed only as an existing file from BaseModels, verified by hash. The process encoding is UTF-8
(`PYTHONUTF8=1`, `PYTHONIOENCODING=utf-8`), because libraries print non-ASCII characters.

End users need neither a system Python nor Node.js. The worker ships built (PyInstaller onedir) in the application directory next to the
host; development uses the interpreter of the pinned environment. Versions of torch, ultralytics and their dependencies are pinned in
`python/requirements.txt`; a runtime update is a separate change with a check of training and prediction.

The GPU is a shared resource: one GPU operation runs at a time, the others wait or get a clear "GPU busy" refusal. The Jobs queue decides,
not the worker.

## Build

The onedir is built by `python/scripts/build-worker.ps1 -Python <python.exe>` into `.dev/python-worker/maut-training-worker/`. The delivery
does not accept a worker built from other sources: `BUILD.txt` lies next to the exe, and the installer build compares it with the current
tree.

```text
format=1
commit=<git rev-parse HEAD>
dirty=false
sourceHash=sha256:<64 hex>
builtAt=<UTC time>
buildSeconds=158
python=3.14.7
pyinstaller=6.22.3
```

UTF-8 without BOM, `key=value` lines separated by LF; a reader ignores unknown keys, and a new mandatory key changes `format`. `commit` is
`git rev-parse HEAD` (or `unknown`), `dirty` tells whether hashed paths have uncommitted or untracked changes (`true|false|unknown`),
`builtAt` is UTC. The hash and the git state are taken before PyInstaller starts.

`sourceHash` is the hash of everything from the repository that goes into the build. The set of paths (from the repository root):

- `python/worker/**` — all files except `__pycache__` folders and `*.pyc`, `*.pyo`;
- `python/worker_entry.py`, `python/maut-training-worker.spec`, `python/requirements.txt`, `python/requirements-build.txt`;
- `catalog/yolo-catalog.json` — the fallback copy of the catalog in the build.

Tests, `tools/`, `scripts/` and the README are not hashed: they do not affect the onedir content. Algorithm:

1. For each file — SHA-256 of its bytes after replacing CRLF with LF (the hash does not depend on `core.autocrlf`), lowercase hex.
2. A manifest line is `<hex>  <path>\n`: two spaces, the path with `/`.
3. Lines are sorted by path ordinally (byte-wise, case-sensitive).
4. `sourceHash` is SHA-256 of the manifest in UTF-8, lowercase hex, prefixed with `sha256:`.

There is one implementation — `python/scripts/worker-source-hash.ps1` (Windows PowerShell 5.1 and newer). Run without dot-sourcing, it
prints the hash; dot-sourced, it provides `Get-WorkerSourceHash`, `Read-WorkerBuildInfo` and `Assert-WorkerBuildCurrent`; the script has no
param block, so it does not overwrite the caller's variables.

```powershell
. .\python\scripts\worker-source-hash.ps1
Assert-WorkerBuildCurrent -Repository . -WorkerDirectory .dev\python-worker\maut-training-worker   # throws without BUILD.txt or on another hash
```

Only `sourceHash` is compared: "older than the sources" means "not built from these sources"; time and commit only explain the refusal.
Packages of the venv are not hashed: their versions are pinned in `requirements.txt`, and a runtime update changes that file.

**onnxruntime is not part of the build** (`excludes` in the spec). It would only come in through lazy imports of onnxslim, `torch.onnx` and
`ultralytics.nn.backends.onnx` and take 223 MB. The worker does not use it: checks run on `best.pt`, and the `best.onnx` export
(`simplify=True, dynamic=True`) produces the same graph without it — the same nodes, initializers, inputs and outputs; only intermediate
`value_info` shape annotations differ. During export Ultralytics prints the expected warning
`requirement ['onnxruntime'] not found, AutoUpdate disabled` to stderr. If the worker starts executing `.onnx`, the exclusion is lifted.

## After a crash

If the application exited during an operation, the job is not considered running at the next start just because of its saved status.
Core checks the job folder and result.json: a valid published result is accepted, otherwise the job gets the interrupted status. Training
is not repeated automatically; the user starts a retry.
