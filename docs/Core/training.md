# Training and model versions

`IProject.Training` holds training runs on dataset versions and the resulting model versions. It works only for an open project
(`training.project_not_open`). Contracts are in `Interfaces/Projects/Training`, the implementation in `Projects/Training`. Parameter
presets are shared by all projects (`ICore.TrainingPresets`, `Training/`), GPUs come from `ICore.Hardware`, the Python executor from
`Core/Python`.

```csharp
var input = await project.Training.PrepareRunAsync(new TrainingRunRequest(datasetVersionId, parameters, presetName), operationId);
var job = await core.Jobs.EnqueueAsync<ITrainingRunJob, TrainingRunInput>(input, project.Id, operationId);
var run = await project.Training.GetRunAsync(job.Id);          // the run ID is the job ID
var history = await project.Training.ListEpochsAsync(job.Id, after: null, limit: 500);
```

Training reads datasets and base models in one direction; they know nothing about training.

## Parameters

Keys are the camelCase keys of the worker protocol (`epochs`, `batch`, `device`, `lr0`, …). `TrainingParameterRules` holds the supported
keys, ranges and defaults tuned in the worker: AdamW, cls 1.0, warmupBiasLr 0, no mosaic or flips, mild hsv, scale 0.1, translate 0.05,
momentum 0.9. `lr0`, `warmupEpochs`, `patience` and `workers` default to null — the worker computes them, and the actual values come back
in `effectiveParams`. The architecture and `imgsz` (the larger side of the project input) are set by the application; `profile` and
`compile` are not supported by the pinned runtime. An invalid or unknown key — `training.parameter_invalid` with `Details["field"]`. The
UI keeps the same keys with labels in `ui/src/api/projects/training/training-parameters.ts`; smoke checks that keys and defaults match.

A preset is a name (unique case-insensitively; saving with the same name overwrites) and validated parameters; the file
`training/presets.json` of the work directory is written through a temporary file and a rename.

## Run

The job `training.run`: a GPU resource (one GPU job at a time; the queue is kept in project.db and survives a restart), not resumable.
`PrepareRunAsync` checks the worker environment, the parameters, the installed base model and that the input is a multiple of its stride,
the dataset (`GetTrainingSourceAsync`) and the `classes` indexes; repeating an operation returns its input.

1. **Preparation** (`preparing`): the folder `projects/<id>/training/jobs/<runId>/` — `input/dataset` (`MaterializeForJobAsync`, hard
   links), `input/model/<arch>.pt` (a copy of the weights after `VerifyIntegrityAsync`), `input/manifest.json`, `request.json` with all
   parameters and relative paths.
2. **Training** (`training`): worker events become job progress (`progress.epoch` with epoch and total, batches within an epoch) and
   live fields of the run.
   Stages, epochs, console lines, effectiveParams and the environment are collected in memory and written as a batch at most every
   500 ms in one transaction; each write is one `TrainingChanged` event with a new run revision.
3. **Acceptance**: the validated result is moved to `files/models/<versionId>/`, then one transaction creates the version (number,
   passport, files with SHA-256, "working" if there is no working version yet), the epoch history is replaced by history.jsonl, and the
   run gets `resultVersionId`. The job folder is removed. On failure and cancellation `input/` and `output/` are removed; `logs/` stays.

The run state is derived from the job and its phase: queued, preparing, training, cancelling, succeeded, failed, cancelled,
interrupted. A worker error is stored with the code `training.worker_<code>`; `error_message` keeps what the UI shows for it — the
worker's code with its reason and args and the English message (`CodedTextJson`, see below); executor errors are `training.protocol_*`,
`training.result_*`, `training.artifact_*`, `training.worker_crashed` and others with an English message for the log (the UI shows the
text of the code).

## Training log

The log of a run (`training_log`) has four kinds of lines:

| Kind | Message | Shown |
|---|---|---|
| `log` | a console line of Ultralytics as the worker sent it | as is: a technical log, not translated |
| `stage` | the worker stage (`training`, `finalizing`…) | as is |
| `system` | a line of the application: a code of the UI catalog `trainingLog` with values | in the language of the user |
| `warning` | a worker warning: a code `worker.<code>[.<reason>]` with args and the English message | "code: text" in the language of the user |

```csharp
recorder.Log(TrainingLogKind.System, CodedText.Of("trainingLog.version_created", ("number", 3)));   // «Создана версия модели 3.»
```

System lines and warnings are stored in `training_log.message` as the `CodedTextJson` object, console lines and stages as text. Lines of
1.0.0 (Russian text without a code) are read as text and shown as they are. Notes of a model version (`model_versions.notes`) are worker
warnings stored the same way, one JSON string per note.

While a run is queued, preparing or training, deletion of its dataset is blocked by `training.active` (the `DatasetBlockers` registry).
Finished model versions do not block the dataset: the dataset version passport stays.

## After the application exits

When the project opens, Jobs turns a running run into `interrupted`. Then training checks its folder: a valid published
`output/result.json` of this operation is accepted as an ordinary result, and the job becomes `succeeded`; otherwise the run stays
interrupted, and its saved epochs and log are kept. There is no automatic retry. Folders of finished runs are cleaned in the background.

## Model versions

A version has a number in the project, the run, a reference to the dataset and its classes, architecture and input, requested and actual
parameters, completed epochs and early stopping, the best epoch (by fitness), classes in the order of model outputs (with `singleCls` —
one merged class with the list of the original ones), best.pt metrics, files (best.pt, best.onnx, plots, log), notes — worker warnings
as codes,
and the environment (GPU, torch, ultralytics, CUDA). There is one working version. Pictures are served by the resource channel, kind
`models`.

**Deletion.** `ReviewVersionDeletionAsync` → `DeleteVersionAsync`. The working version and a version that is being evaluated, checked
live or exported cannot be deleted: `training.version_in_use`, `Details["reason"]` — working | evaluation | check | export. The reasons
other than working, and the rows deleted together with the version (its evaluations and their jobs), are registered by the Core
composition (`ModelVersionLinks`): training knows nothing about checks. The version row and its files list are deleted in one
transaction, the folder `files/models/<versionId>/` in the background (`FileCleanup`). Version numbers are never reused.

**ZIP export.** The job `training.export` (CPU): "Save as" in the host, then `PrepareExportAsync` with an absolute path. The archive
contains `weights/best.pt`, `weights/best.onnx` (if present), `manifest.json` (passport, `schemaVersion: 1`), `classes.json` (output map)
and `README.md` in English (the archive is shared with others, whatever the language of the interface; notes in `manifest.json` are
"code: English message"); no plots or log. Weights are checked by size and SHA-256 while packing (`training.artifact_corrupt` with
`Details["name"]`). Entries go in a fixed order with a fixed date: exporting the same version again gives the same bytes. The archive is
written to a temporary file next to the target and replaces it at the end; the job result is the name, size and SHA-256 of the archive.

```text
projects/<projectId>/
  training/jobs/<runId>/        the job folder while the run lasts (after a failure — logs/)
  files/models/<versionId>/     best.pt, best.onnx, training.log, plots/*.png|jpg
```

## Python executor

`PythonWorkers` starts the worker as described by `CoreOptions.PythonWorker` ([Python worker](../Python/worker.md)) and returns the
operation outcome; a code without an area prefix (`worker_out_of_memory`, `protocol_sequence`) is prefixed by the consumer. The same
executor serves [model checks](checks.md).
