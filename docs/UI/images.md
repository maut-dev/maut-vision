# Viewer, annotation and image gallery

## Shared viewer

`components/viewer/image-viewer.tsx` is one frame viewer for images, classes, backgrounds and datasets. The owner passes a source with a
resource channel URL, the frame size (accounting for orientation), boxes and details; the owner handles "previous"/"next" and loading of
further pages.

```tsx
<ImageViewer
    source={{ id: image.id, name: image.displayName, width: image.width, height: image.height,
              src: image.originalUrl, renew: () => gallery.renew(image.id), overlays }}
    close={() => setViewer(undefined)}
    onNext={hasNext ? next : undefined}
    position={`${index + 1} / ${items.length}${hasMore ? "+" : ""}`}
/>
```

- Boxes are fractions 0–1 from the top-left corner of the frame. The ROI is drawn with `#fcd34d` and the dash "6 4".
- Zoom (`lib/annotation/zoom.ts`, the `useZoomViewport` hook) is shared with the editor: "fit" = min(1, width/w, height/h) without
  enlarging, x1 is one frame pixel per CSS pixel, steps up to x10, and the centre of the visible area is kept.
- Esc in full screen exits it first; after closing, focus returns to the element that opened the viewer.
- An expired URL: the viewer calls `renew` once, then offers to retry loading.

## Annotation editor

Geometry and edit history are pure modules `lib/annotation/{geometry,history,draft}.ts` without React. Pointer coordinates are computed
from the screen rectangle of the frame (`pointInFrame`), so they are correct at any zoom and scroll. Boxes stay inside the frame after any
action; a box smaller than 0.002 is not created (an accidental click).

A draft is unsaved only if it differs from the saved revision: undoing back to the saved state clears the flag. A drag or resize goes into
the history as one step.

## Unsaved changes

`UnsavedChangesProvider` (`components/unsaved-changes.tsx`) is one guard for all transitions. A screen registers the flag, save and discard
(`useUnsavedChanges`); any transition that may lose changes goes through `useNavigationGuard()`. A page reload asks through `beforeunload`,
closing the application window through the host request ([navigation](navigation.md)).

```tsx
const guard = useNavigationGuard();
<Button onClick={() => guard(() => setView(next))}>…</Button>
```

## Gallery

The gallery renders only the visible rows of a fixed-height grid, so the number of frames in a group does not limit the interface. Pages
are read by cursor while scrolling. No polling: `images.changed` with `imagesChangedIn` rereads only the tail of the list (new frames go to
the end of the import order), changed images from the event are reread one by one, deleted ones are removed. Operations on a selection are
limited to 400 images, as in the API.

## Video frames

The "frames from video" action of a group opens a panel above the gallery (`components/images/video-frame-import.tsx`); it is unavailable
where file import is unavailable (archive, generated group, running generation). The video is chosen in a native window
(`imagesApi.selectVideo`); the panel shows name, size and duration and the fields start (0 s), interval (1 s) and frame count
(min(20, max(1, ⌈duration in seconds⌉))). Values are checked by `videoFrameRangeError` — the same rules as in the host: a last timestamp
past the end of the video shows an error and disables extraction.

```tsx
const range = { startMs: Math.round(start * 1000), intervalMs: Math.round(interval * 1000), count };
const error = videoFrameRangeError(range, video.durationMs);
await imports.importVideo(video.selectionToken, range, skipDuplicates);
```

Progress and outcome are shown by the shared import history of the group (`useImageImports`). A video selection serves one run: afterwards
the panel asks to choose the video again. The "skip duplicates in the group" flag is shared with file import.
