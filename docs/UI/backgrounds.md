# Backgrounds in the UI

The "Backgrounds" section (`components/backgrounds/`) shows sets on the left and the content of the selected set on the right: actions,
"add backgrounds", the gallery and the import history. Panels scroll separately; "add backgrounds" and the history collapse so that the
gallery stays visible in a window of about 1200 × 760. Data comes from the [backgrounds API](../Core/backgrounds.md), without polling:
`backgrounds.changed`, `job.changed`, `project.changed`.

```tsx
const sets = useBackgroundSets(projectId);                 // backgrounds.changed + project.changed (matchesProjectInput)
const gallery = useBackgroundGallery(projectId, setId, showRemoved ? "all" : "active");
const runs = useBackgroundRuns(projectId, setId);          // listRuns + job.changed, this set only
```

## Adding

Three methods in a radio group; the blur (`useBackgroundBlur`) is shared by all of them and is not reset when the method changes. Each run
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
with Shift by 10 px.

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
and cancellation) and `ReviewDeletionDialog` (two-phase permanent deletion) are used by the images and backgrounds sections. A new gallery
or history of a long operation is built on them.
