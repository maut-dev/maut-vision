# Checks in the UI

The "Checks" section (`components/checks/`) has a version choice (the working one by default) and the modes "Image", "Dataset", "Video" and
"Screen" following the tablist pattern. The open mode stays mounted: switching tabs does not interrupt a check. Data comes from
[model checks](../Core/checks.md), without polling.

```tsx
const session = useLiveCheck(projectId, "screen", { keep: 40, stopOnLeave: true }); // snapshot + checks.changed by sequence
const evaluations = useModelEvaluations(projectId);   // listEvaluations, listLatestEvaluations, checks.changed, job.changed
const gpus = useGpuInventory();                       // "Device": the training list without auto selection
```

## Shared fields

Confidence threshold (0–1), detections per frame (empty — automatic) and device (`DeviceSelect`: "CPU" and GPUs, the first by default;
without a driver — manual input). Validation before sending is `parseCheckParameters`, the same rules as in Core; start errors show the
host text in the mode that caused them (for example, a busy GPU).

## Live checks

`useLiveCheck` applies a snapshot only with a greater `sequence`. New journal lines are read with `listJournal` and `after` when the event
reports `journal.lastOrdinal` greater than the loaded one; memory holds at most `keep` latest lines, earlier ones by `before`. On mount a mode
finds its latest check (`list` → `get`), so returning to the section shows a running or finished check.

- Previews and boxes: resource channel URLs, boxes in frame fractions over the preview, class colour `--series-N` by index.
- Video and screen larger than the input get a crop of the input size (the `BackgroundCropFrame` of backgrounds); otherwise the whole frame
  is checked.
- A screen check stops when the section is left (`stopOnLeave`); a project switch and a page reload are handled by the host.

## Dataset

`ModelEvaluations` is shared by "Checks" and "Analytics": a form with compatibility hints (`evaluationProblem` repeats the Core rules), a
matrix comparing the selected versions on one dataset by the latest successful evaluation, and the list of all evaluations with cancellation
and per-class metrics. The owner passes the matrix versions and the form version.
