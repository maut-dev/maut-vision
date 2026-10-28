# Training in the UI

The "Training" section (`components/training/`) has the tabs "Settings", "Current run" and "Training history" following the tablist
pattern, as for datasets. All panels stay mounted: the form survives switching, and the run passport receives events while hidden. Data
comes from the [training API](../Core/training.md), without polling.

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

The run state comes from the job snapshot `job.changed` immediately (`withJob`), details from `getRun` by the `training.changed` revision.
Epochs are read further when `epochCount` exceeds the loaded ones, the log when `logCount` grows; after the outcome the epoch history is
reread completely (Core replaces it with the worker file).

## Epoch charts

`TrainingEpochChart` is an SVG without dependencies: a fixed series palette, validation losses dashed in the colour of their train series,
a legend that toggles series, a crosshair with an "Epoch N" tooltip. There is no 200-epoch window: the whole run is drawn, and `thinPoints`
(`lib/training/epoch-chart.ts`) keeps the minimum and maximum of each bucket, at most two points per pixel. The tooltip takes values from
the full series.

## Training history

Run cards with state, dataset, parameters, progress and error; "run passport" opens "Current run", "version N passport" opens the same run
with the version passport. State labels are `trainingRunStateLabels`, one set for all screens.
