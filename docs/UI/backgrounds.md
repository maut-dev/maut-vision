# Backgrounds in the UI

The "Backgrounds" section (`components/backgrounds/`) is a master-detail section: sets on the left, the chosen set on the right. Data
comes from the [backgrounds API](../Core/backgrounds.md), without polling: `backgrounds.changed`, `job.changed`, `project.changed`.

```tsx
const sets = useBackgroundSets(projectId);                 // backgrounds.changed + project.changed (matchesProjectInput)
const gallery = useBackgroundGallery(projectId, setId, showRemoved ? "all" : "active");
const runs = useBackgroundRuns(projectId, setId);          // listRuns + job.changed, this set only
```

## Layout

The section follows [one scroll per region](design-system.md#one-scroll-per-region): the list of sets scrolls on its own, the set column
does not scroll. The set column has a compact header (name, summary, the size mismatch note, edit, archive, delete) and three tabs.

| Tab | Holds | Scrolls |
|---|---|---|
| «Фоны» (count of backgrounds) | the selection toolbar and the virtual gallery | the gallery |
| «Добавить» | the add form; at `lg` and wider the running tasks of the set beside it | the form; the task column |
| «Подготовка» (count of runs, a live dot while a job is queued or running) | the full run history of the set | the history |

A set opens on «Добавить» when it is empty and on «Фоны» otherwise; the choice is made when the set is chosen, so later counts do not
switch the tab under the user. Starting a run keeps the user on «Добавить»: the task column (or, below `lg`, a strip with the progress
of the running jobs and a button to «Подготовка») shows it at once. An archived set disables «Добавить» and shows the gallery; restoring
the set returns to the chosen tab.

«Добавить» stays mounted while another tab is shown (`forceMount` with `hidden`): the chosen ready files, the video with its example
frame and frame position, the counts and the blur survive switching tabs. The other tabs mount when they are shown.

```tsx
<TabsContent value="add" forceMount hidden={shownTab !== "add"}>…</TabsContent>
```

Skip reasons of a run unfold inline in its row (`JobIssues`), without a scroll of their own. The run/import button of the chosen way of
adding is the one brand button of the section.

## Adding

Three methods in a segmented radio group with short names («Готовые», «Изображения», «Видео») and the hint of the chosen one
under it, so the choice takes one row of the form; the blur (`useBackgroundBlur`) is shared by all of them and is not reset when the method changes. Each run
is one `backgrounds.import` job; the job appears in the set history immediately, and progress and outcome arrive as events.

- Ready frames: after selecting files, before the run, a summary from the headers shows how many files have the right size and how many
  will be skipped.
- Crops from images: the dialog goes through files one by one. Decisions by header are made without loading the frame (exact size —
  whole, smaller — skipped, unreadable — listed). The crop position: an earlier decision for this file → the last position for a file of
  the same size → the centre.
- Crops from video: one crop per video; a video selection serves one run.

Samples and the run use one `operationId`, so samples match the future backgrounds. Field checks before sending use the rules of
`background-import-rules.ts` (the same as in the host); message texts are in `lib/backgrounds/*`.

## Crop frame

Pure geometry is in `lib/backgrounds/crop-frame.ts`: coordinates in pixels of the source frame; a preview of up to 1,600 px is mapped
through fractions of the screen rectangle. The frame is a `role="slider"`: dragging puts the centre under the pointer, arrows move by 1 px,
with Shift by 10 px. Its width is limited by the column and its height by a share of the window, so it never needs a scroll of its own.

```ts
const next = nudgeRegion(value, event.key, event.shiftKey, source, frame);   // null — not an arrow key
const start = defaultRegion(source, frame, previousRegion(decision), last);
```

## Gallery and viewer

The gallery is the shared virtual grid `components/virtual-grid.tsx` (the same as for images); thumbnails come from the resource channel.
Selection is `useRangeSelection` (Shift ranges, "select all on page", up to 400). A removed background is shown only with "show removed"
and does not open in the viewer; the viewer is the shared `ImageViewer` with rows for source, region and blur.

## Shared parts

`VirtualGrid`, `useRangeSelection`, `useJobHistory` (history of jobs of one kind for one target), `JobRunRow` (a history row with progress
and cancellation), `JobHistory`, `JobIssues` and `ActiveJobs` (`components/jobs/`), the list, item and create form of
`components/collections/collection-list.tsx` and `ReviewDeletionDialog` (two-phase permanent deletion) are used by the images and
backgrounds sections. A new gallery, history of a long operation or master-detail list is built on them.
