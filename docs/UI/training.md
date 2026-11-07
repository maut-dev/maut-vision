# Training in the UI

The "Training" section (`components/training/`) fits the window and has the tabs "Settings", "Current run" and "Training history"
following the tablist pattern, as for datasets. All panels stay mounted: the form survives switching, and the run passport receives events
while hidden. The history tab counts the runs and shows a live dot while a run is queued or running. Data comes from the
[training API](../Core/training.md), without polling.

| Tab | What scrolls |
|---|---|
| Settings | the tab: the form in one scroll |
| Current run | at `lg` and wider two columns: the passport and the training log, each with its own scroll; below `lg` they stack in the scroll of the tab |
| Training history | the tab: the run cards and "more runs" |

```tsx
const runs = useTrainingRuns(projectId);          // listRuns + training.changed (revisions → getRun) + job.changed (kind training.run)
const versions = useModelVersions(projectId);     // listModelVersions, reread on training.changed with versions
const setup = useTrainingSetup();                 // presets (trainingPresets.changed), getRuntime, listGpus
const details = useTrainingRunDetails(projectId, run); // epochs and log of the selected run with after cursors
```

## Settings

The form is `lib/training/training-form.ts`: numbers and lists are kept as typed text, flags and choices as values; `parseTrainingForm`
gives the request parameters and the invalid keys with the same rules as Core (`training-parameters.ts`). Labels, groups, ranges and
defaults come only from the parameter registry.

```ts
const parsed = parseTrainingForm(form);               // { parameters, invalid }
const tuned = formTunedDefaults(form);               // lr0, warmupEpochs, patience, workers for "auto"
```

- An empty "auto" field is sent as null; the field shows the value the worker will compute. With the auto optimizer `lr0` is unavailable
  and marked as chosen by the optimizer.
- "Device" is the `deviceOptions` list from `listGpus`; without a driver — "CPU" and manual input. A value missing from the list gets its
  own item: Radix Select inside a form resets such a value to empty.
- "Rotations" is a toggle before the degrees field (`toggle` in the registry).
- Dataset warnings come from `trainingFormWarnings`; blocking ones disable "start training".
- A preset fills the form. Changing the form after choosing a preset clears the choice (the preset can be applied again), and the run
  starts without a preset name.
- If the worker environment is not configured or not found, a notice shows the host text and the run is unavailable. A host response with
  `details.field` highlights the field.

## Current run

The run passport: progress ("Epoch X of Y", "Batch a of b", a bar, live metrics, the stage), waiting and preparation, error, environment,
dataset and classes, epoch history, run parameters, training log and cancellation; after success — a short passport of the model version
(metrics, best epoch, parameters, files, pictures in the shared viewer).

The log column keeps its newest lines in view while "latest messages" is shown. The version passport has no scroll of its own: the output map
of classes is listed in full and adapts to the width of the column through container queries.

The run state comes from the job snapshot `job.changed` immediately (`withJob`), details from `getRun` by the `training.changed` revision.
Epochs are read further when `epochCount` exceeds the loaded ones, the log when `logCount` grows; after the outcome the epoch history is
reread completely (Core replaces it with the worker file).

## Epoch charts

`TrainingEpochChart` is an SVG without dependencies: the `series-N` tokens of the design system (axes and grid use the line and text
tokens), validation losses dashed in the colour of their train series,
a legend that toggles series, a crosshair with an "Epoch N" tooltip. There is no 200-epoch window: the whole run is drawn, and `thinPoints`
(`lib/training/epoch-chart.ts`) keeps the minimum and maximum of each bucket, at most two points per pixel. The tooltip takes values from
the full series.

## Training history

Run cards with state, dataset, parameters, progress and error; "run passport" opens "Current run", "version N passport" opens the same run
with the version passport. State labels are `trainingRunStateLabels`, one set for all screens.
