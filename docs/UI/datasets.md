# Datasets in the UI

The "Datasets" section (`components/datasets/`) has the tabs «Датасеты», «Сборка», «Проверки» and «Экспорты» (`Tabs`: one tab stop,
arrows wrap around, Home and End). Each tab counts its items, and an operation tab shows a live dot while an operation is queued or running.
All panels stay mounted (`forceMount` with `hidden`): the form and the plan survive switching, and operation cards receive events while
hidden. The tabs and panels keep the ids `dataset-tab-<tab>` and `dataset-panel-<tab>`, so focus can move to a tab by its id. Data comes
from the [datasets API](../Core/datasets.md), without polling.

```tsx
const versions = useDatasetVersions(projectId);           // list (archive included) + datasets.changed (revisions, null — deleted)
const builds = useDatasetOperations(projectId, "build");  // listOperations + job.changed (kind datasets.build, sequence)
const samples = useDatasetSamples(projectId, versionId, split); // sample pages on demand
```

## Layout

The section follows [one scroll per region](design-system.md#one-scroll-per-region).

| Tab | Holds | Scrolls |
|---|---|---|
| «Датасеты» | search and "Show archive" above the list; the chosen version with its header and the tabs «Сведения» and «Образцы» | the list; the passport or the sample grid |
| «Сборка» | the build form; beside it the plan and the build history | the form fields; the plan with the history |
| «Проверки», «Экспорты» | operation cards | the card list |

At `lg` and wider the build form and the plan with the history are two columns; below `lg` they stack in the one scroll of the tab. Image
groups and classes flow in the form scroll: classes are a grid of checkboxes with "Select all", "Clear all" and "N of M chosen", so a
hundred classes take a few dozen rows and need no list or dialog of their own. "Check before building" stays under the fields; "Build
dataset" on the plan card is the brand button of the section. Integrity issues and the details of an operation unfold inline in its card.

## Datasets

On the left is the list with search by name and description and an option to show archived datasets; a version whose size differs from the
project input is marked. On the right is the chosen version: a header with its name and actions, then «Сведения» (the passport: splits,
integrity, build, class order, composition) and «Образцы» (the sample grid, the only scroll of that tab). «Сведения» stays mounted; the
chosen tab survives choosing another version. An archived version cannot be checked, exported or changed, a corrupt one cannot be exported;
Core checks the same.

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
moves it to the operation tab. The focus moves after the render in which the section is no longer busy (`useFocusAfterRender`), so a
button disabled during the action takes it.
