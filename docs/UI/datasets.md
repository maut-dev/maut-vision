# Datasets in the UI

The "Datasets" section (`components/datasets/`) has the tabs "Datasets", "Build", "Checks" and "Exports" following the tablist pattern:
`role="tab"`, one tab stop, arrows wrap around, Home and End. All panels stay mounted: the form and the plan survive switching, and
operation cards receive events while hidden. Data comes from the [datasets API](../Core/datasets.md), without polling.

```tsx
const versions = useDatasetVersions(projectId);           // list (archive included) + datasets.changed (revisions, null — deleted)
const builds = useDatasetOperations(projectId, "build");  // listOperations + job.changed (kind datasets.build, sequence)
const samples = useDatasetSamples(projectId, versionId, split); // sample pages on demand
```

## Datasets

On the left is the list with search by name and description and an option to show archived datasets; a version whose size differs from the
project input is marked. On the right are the passport of the selected version, actions and samples. An archived version cannot be checked,
exported or changed, a corrupt one cannot be exported; Core checks the same.

Samples are a `VirtualGrid` of split thumbnails with boxes: objects solid, negatives and ROI dashed, colours from
`datasetOverlayColors`. A frame opens in the shared viewer with toggles for objects, negatives and ROI; "next" past the last loaded sample
reads the next page, and the position is "i / N" of the version content. Object labels are the version class and the variant name at build
time (`version.variants`).

## Build

The form is `lib/datasets/dataset-form.ts`: values → specification (`toSpecification`, variant roles — `datasetVariantRoles`),
specification → form for "configure repeat" (`formFromSpecification`). Validation before sending is `specificationFieldError` (the host
rules); a response with `details.field` highlights the field.

```ts
const specification = toSpecification(form, catalog.workspace);
const stale = plan && planKey !== specificationKey(specification);   // "build dataset" is unavailable
```

- Groups show the number of active frames, origin, split hint and the share of reviewed frames. The group DTO has no reviewed counter:
  `useGroupReviewCounts` counts it with `images.list` pages with `reviewState = reviewed` for groups up to 2,000 frames while the tab is
  open and recounts on `images.changed`; for larger groups the plan gives the number.
- The plan card shows splits, skipped frames with reasons and host warnings. "Build dataset" is unavailable if the plan is not
  `buildable` (identical pixels in different splits), sources are insufficient or the form changed after the check.
- The plan is compared with the sent specification, not with its copy in the response.

## Operations

A build, check or export card (`DatasetOperationCard`) shows progress and result from the latest job snapshot (`operationProgress`: the
result wins over `processed/total`), duration, integrity problems and the export file. "Open dataset" switches to "Datasets", selects the
version (showing the archive if needed) and moves focus to it. Export is `selectExportDestination` (native "Save as"; a closed window starts
nothing) → `startExport`; "save again" repeats both steps.

Dataset deletion uses `ReviewDeletionDialog` (content and blockers, then deletion with the revision); its operations leave the lists with the
version. "Remove from history" removes only the record of a finished operation.

## Accessibility

Dialogs and the viewer return focus to the opening element. "Configure repeat" moves focus to the form name; starting a check or export
moves it to the operation tab.
