# Models in the UI

The "Models" section (`components/models/`) lists all model versions of the project, newest first: a card with metrics, the working
version, ZIP export, deletion and the passport. Data comes from [model versions](../Core/training.md#model-versions), without polling.

```tsx
const models = useModelVersions(projectId);    // listModelVersions + training.changed (versions, revision null — deleted); loaded, listedAt
const exporter = useModelExport(projectId);    // selectModelExportDestination → startModelExport → job.changed (kind training.export)
```

## List

- Metrics mAP50, mAP50-95, precision and recall use the format 0.000; "best" is marked only if there are at least two numbers and they
  are not all equal (`bestOf`, `lib/checks/check-view.ts`).
- The dataset of a version is named from the current dataset list: archived ones are marked, a missing one is shown as not found.
- The empty state leads to training.

## Export and deletion

"Export ZIP" first opens the native "Save as"; a closed window starts nothing. Progress and result are job snapshots by `sequence`,
including those that arrive before the start response. The result is a notice with the file name and size.

Deletion uses the shared `ReviewDeletionDialog`: content (files, dataset evaluations) and blockers with host texts. A
`training.version_in_use` refusal received at deletion time is shown with the reason from `details.reason` (working, evaluation, check,
export).

```ts
if (reason && versionInUseReasons[reason]) throw new Error(`Версию сейчас нельзя удалить. ${versionInUseReasons[reason]}`);
```

## Passport

The passport is the shared `ModelVersionPassport` of training; in "Models" it gets export, deletion and "back to models", and the epoch
history is read in pages by `runId`. The navigation intent `{ section: "models", versionId }` opens the passport directly.

A training run keeps `resultVersionId` after the version is deleted. The training history shows "version deleted" if the version is missing
from the list read after the run ended (`listedAt`): a version created a moment ago is not taken for a deleted one.
