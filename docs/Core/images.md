# Images and annotation

`IProject.Images` holds the image groups of a project, the images and their annotation. It works only for an open project
(`images.project_not_open`). Contracts are in `Interfaces/Projects/Images`, the implementation in `Projects/Images`.

```csharp
var project = await core.Projects.OpenAsync(projectId);
var group = await project.Images.CreateGroupAsync("Улица", "", ImageSplitHint.Train);
var job = await core.Jobs.EnqueueAsync<IImportImagesJob, ImportImagesInput>(
    new ImportImagesInput(group.Id, [new ImageImportSource(ImageImportSourceKind.Folder, folder, Recursive: true)]),
    projectId, operationId);
var page = await project.Images.ListImagesAsync(group.Id, new ImageFilter(), limit: 100);
var boxes = await project.Images.ListAnnotationsAsync(page.Items.Select(image => image.Id).ToArray());
```

## Groups and images

- A group has a name of 1–120 characters (duplicates allowed), a description up to 4,000, a split hint
  (`unassigned | train | validation | test`) and an origin `manual | generated` set at creation. The API creates only manual groups;
  a result group is created by a [generation run](generation.md#run) (`GenerationRunId`, `GenerationState`:
  `producing → completed | partial`). A group being filled by generation (`producing`) is read-only: `images.group_busy`, archiving
  included. Its images arrive with the `reviewed` annotation.
- An image keeps the original file unchanged: `files/images/<id>.<ext>`, PNG, JPEG, WebP or BMP. Width and height account for EXIF
  orientation; thumbnails and `<img>` apply it the same way, so annotation coordinates refer to the oriented frame.
- The order within a group is the import order (`ordinal`); moved images go to the end of the target. Pages use a cursor (`after`); the
  cursor is opaque and bound to the filter (archive, review state).
- Archiving affects only the object. An archived group is read-only except for restoring and deleting; an archived image cannot be
  annotated or moved.
- Moving works between groups of the same origin, all or nothing, with the revisions of both groups and of every image.

## Revisions and events

Groups, images and annotations have revisions; a change passes the one the user saw, a mismatch is `images.revision_conflict`. The
group revision changes with its fields, archiving and moving; an import does not require the group revision and changes only counters.

`IProjectsManager.ImagesChanged` reports what changed: groups with revisions (null — deleted), images of explicit operations and
`ImagesChangedIn` — groups whose content changed. The UI rereads only those. Import reports changes at most every 500 ms per group.

## Annotation

Annotation revisions are immutable: saving creates the next one (`AnnotationRevision`), and the history stays readable. Import creates
revision 1 without objects.

```csharp
await project.Images.SaveAnnotationAsync(image.Id, new AnnotationInput(
    [new AnnotationObjectInput(null, classId, classVariantId, new ImageBox(0.1, 0.2, 0.3, 0.4))],
    Roi: null, ImageReviewState.Reviewed), image.AnnotationRevision);
```

- A box is in fractions 0–1 from the top-left corner, with a positive size; the right and bottom edges are at most 1 + 1e-9. An
  invalid box is rejected (`images.box_invalid`), never clipped.
- A new object arrives without an ID — Core assigns it; an existing one keeps the ID from the current revision
  (`images.object_id_invalid`).
- An object references a class and, optionally, a variant of that class (a binding). New objects and objects whose class or variant
  changed require an active class and variant (`images.reference_unavailable`); unchanged ones keep an archived reference.
- The current annotation blocks deletion of the class, class variant and project variant it references (provider
  `images.annotations` in `CatalogBlockers`). Earlier revisions do not block.
- `ReviewState.Reviewed` with an empty list is a deliberately empty frame.

## Import

The job `IImportImagesJob` (`images.import`, CPU, resumable) serves one user action. The input is a group, a list of sources and the
duplicates flag; Desktop gives the UI only a selection token.

- Source `File` is one file; `Folder` — the PNG/JPEG/WebP/BMP files of a folder by relative path in natural order (`ImageNameOrder`:
  frame2 before frame10), with `Recursive` also of subfolders; `Video` — video frames (below). Links, hidden and system entries are not
  read. A new source kind is added as an `ImageImportSourceKind` value and its expansion in `ImportPlan`; the job input and result do
  not change.
- Each file is read once and fully decoded once: format, size with orientation, a check of all pixels, `PixelHash` — SHA-256 of the
  oriented RGBA pixels with the size. A broken file is rejected with a reason in the result.
- `SkipDuplicates` (on by default) skips frames whose pixels are already in the group, including the same frame in another format.
- Batches of 64 files: several threads prepare files, then files are renamed into place, the rows of the batch are inserted in one
  transaction and a checkpoint is saved. The image ID is derived from the job ID and the file number, so a repeat after a crash finds
  its rows.
- After a restart the job expands the sources again; if folder contents changed, it ends with `images.import_sources_changed`, and
  what was added stays.
- Only manual active groups accept import. Cancellation keeps what was added; the result `ImportImagesResult` is in `JobInfo.Result`.
  The import history of a group is `ListImportsAsync`.

## Video frames

Video (MP4, MKV, AVI, MOV, WebM, M4V) is read by the bundled ffprobe and ffmpeg (`ICore.Media`,
[packaging](../Desktop/appdata-and-packaging.md#ffmpeg)). Selection only reads properties (`ProbeVideoAsync`: size with rotation,
duration, frame rate, codec); the file is not hashed.

```csharp
var video = await core.Media.ProbeVideoAsync(path);
var frames = new VideoFrameRange(StartMs: 0, IntervalMs: 1000, Count: 20);
frames.Validate(video.DurationMs);
var input = new ImportImagesInput(group.Id, [new ImageImportSource(ImageImportSourceKind.Video, path, Frames: frames)]);
```

- Timestamps are `StartMs + i × IntervalMs`: start ≥ 0, interval ≥ 1 ms, count ≥ 1 (`video.frames_invalid`), the last timestamp
  strictly before the end (`video.range_invalid`). The job checks this again with a fresh ffprobe.
- The frame of a timestamp is the first frame whose time is not earlier than the timestamp (0.5 ms tolerance), at the original size,
  as PNG named `<video name, up to 160>-frame-<ms>.png`. A timestamp past the end of the video track (the container is longer than the
  picture) rejects the frame: `video.frame_unavailable`.
- Origin `video`: video name, timestamp, frame number, family `video:<SHA-256 of the file>`. The hash is computed in the job in one pass
  over the file in parallel with decoding, so all frames of one file are one family in any import.
- One video of a job is decoded by one ffmpeg process in one pass with the `select` filter; with a step above 3 s each frame is read by
  a separate seek. ffmpeg outputs RGBA (PAM) to a pipe; a frame is read only when an import thread waits for it, so memory holds no more
  frames than threads. Threads hash pixels and write PNG in parallel; then the shared import batches follow.
- Cancellation and failure end the ffmpeg process tree. After a restart the job reads the video again from the first frame after the
  checkpoint; frame IDs are derived from the job ID, so there are no duplicates. A video changed before continuation (size, write time)
  — `images.import_sources_changed`.
- No ffmpeg — `video.ffmpeg_missing`; unreadable — `video.unreadable`; another extension — `video.unsupported`.

## Permanent deletion

A content review (the whole group or up to 400 selected images of one group) and deletion with its revision, as in the
[catalog](catalog.md#permanent-deletion): rows, annotation and (for a group) import history are deleted in a transaction, files and
thumbnails in the background (`FileCleanup`). Only what fills this group blocks: import (`images.import_active`) and generation
(`images.generation_active`, the group name as an example); jobs of other groups do not interfere. A group with a running import
cannot be archived (`images.group_importing`).

## Performance

Indexes cover every combination of gallery filters in import order, the duplicate search by `(group_id, pixel_hash)` and the check of
annotation references to the catalog. Opening a project reads no images. A gallery page and the annotation of a page are one query
each.
