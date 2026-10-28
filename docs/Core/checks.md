# Model checks

`IProject.Checks` checks model versions: evaluation on a dataset split and live checks on an image, a video and the screen. It works
only for an open project (`checks.project_not_open`). Contracts are in `Interfaces/Projects/Checks`, the implementation in
`Projects/Checks`. Model versions and datasets are read in one direction: training and datasets know nothing about checks; their links
are registered by the Core composition.

```csharp
var input = await project.Checks.PrepareEvaluationAsync(new ModelEvaluationRequest(versionId, datasetId, EvaluationSplit.Test, "0"), operationId);
var job = await core.Jobs.EnqueueAsync<IModelEvaluationJob, ModelEvaluationInput>(input, project.Id, operationId);
var check = await project.Checks.StartVideoCheckAsync(new VideoCheckRequest(versionId, source, new LiveCheckParameters(0.25, null, "0"), 1000, region));
```

## GPU

One GPU operation per application: training, dataset evaluation or a live check. Jobs wait for the slot in the Jobs queue (`GpuSlot`).
A live check takes the slot immediately or is refused according to who holds it: `checks.gpu_busy_training`,
`checks.gpu_busy_evaluation`, `checks.gpu_busy_prediction`. Training and evaluation queued during a check wait for its end. The slot is
also taken with the "CPU" device, as for training.

## Dataset evaluation

The job `checks.evaluate`: GPU, not resumable. `PrepareEvaluationAsync` checks the worker environment, the parameters
(`checks.evaluation_parameters` with `Details["field"]`), the dataset (not archived, not corrupt), the classes and their order
(`checks.evaluation_classes_mismatch`), that the frame size equals the version input (`checks.evaluation_input_mismatch`, sizes in
details) and that the split has frames (`checks.evaluation_split_empty`, `Details["split"]`). The job folder is
`projects/<id>/checks/jobs/<evaluationId>/`: the dataset and the weights as hard links (weights after a SHA-256 check) and `request.json`
of the `evaluate` operation. The result is Ultralytics metrics (`model.val`), per-class metrics with the version classes and the number of
split frames; it is kept in `model_evaluations`.

The state is derived from the job and its phase as for training: queued, preparing, running, cancelling, succeeded, failed, cancelled,
interrupted. After the application exits, a running evaluation is interrupted and its result is not accepted. A running evaluation
blocks deletion of the dataset (`checks.evaluation_active`) and of the version.

`ListLatestEvaluationsAsync` gives the comparison matrix the latest successful evaluation of each version on a "dataset · split" pair.

## Live checks

A live check is not persisted: it lives in Core memory while the application runs (the last few finished ones are kept to show the
outcome). One worker process performs the whole check: `predict` for an image and a video, `live` for the screen; the model is loaded
once.

- **Inputs without copies.** The version weights and the selected file are hard links in `input/`; a file on another volume is passed by
  absolute path as `externalSource`. The selection keeps the file size and modification time: a file changed after selection is
  `checks.source_changed`.
- **Parameters.** The confidence threshold (0.25), the number of detections (null — all candidates; also passed for an image) and the
  device from the training GPU list. The region equals the version input and lies inside the frame or monitor (`checks.region_invalid`);
  without a region the whole frame is letterboxed. Video is read sequentially with step `StepMs`; the screen at `Fps` up to
  `MaxDurationSec`.
- **Previews are files.** The worker writes JPEG files to `output/previews/` and names the file in the `frame` event; Core reads it into
  memory and deletes it. Bytes never go through events or the bridge: the host issues a temporary resource channel URL for them.
- **The journal is bounded.** "Frames with detections": for the screen a ring of the last 2,000, for a video the first 5,000 with a
  truncation mark. The per-class summary counts all frames; the worker result replaces it at the end. `GetCheckAsync` returns a snapshot
  with the last journal page, `ListJournalAsync` pages before and after a record.
- **Observation.** Every change increments the snapshot `Sequence`; the `ChecksChanged` event carries the snapshot without the journal at
  most 5 times per second, state transitions immediately.

States: preparing, running, cancelling, succeeded, failed, cancelled. A check is stopped by its button, by opening another project
(`ProjectSwitched`) and by the end of the UI page — reload or window close (`SessionEnded`, called by the host). The screen time limit
ends the check as succeeded with the warning `live_time_limit`. The worker process tree never outlives the application (Job Object).

## Screen

`ICore.Screen` lists monitors and takes screenshots without Python. The thread switches to per-monitor DPI awareness, so coordinates are
physical pixels with any process setting; the monitor order and capture method (GDI BitBlt) match mss in the worker. A screen check
passes the worker the monitor bounds of the screenshot on which the region was chosen: if capture sees the monitor differently, it is
the refusal `checks.worker_capture_unavailable`, not a shifted region. Exclusive full-screen applications are not captured.
