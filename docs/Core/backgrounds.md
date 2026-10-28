# Backgrounds

`IProject.Backgrounds` holds the background sets of a project and their backgrounds: ready PNG files of exactly the set size onto which
generation places objects. There is no scaling or letterboxing: a whole frame of the right size or a crop of that size is taken. It
works only for an open project (`backgrounds.project_not_open`). Contracts are in `Interfaces/Projects/Backgrounds`, the
implementation in `Projects/Backgrounds`.

```csharp
var set = await project.Backgrounds.CreateSetAsync("Улица", "");
var input = new ImportBackgroundsInput(set.Id, [new BackgroundFileSource(path, new BackgroundCrop(120, 40))],
    Blur: new BackgroundBlur(0, 4));
var samples = await project.Backgrounds.PreviewImportAsync(input, operationId);       // the same pixels the run produces
var job = await core.Jobs.EnqueueAsync<IImportBackgroundsJob, ImportBackgroundsInput>(input, project.Id, operationId);
```

## Sets and backgrounds

- A set has a name of 1–120 characters (duplicates allowed), a description up to 4,000 and `TargetSize` — a copy of the project input
  at creation that never changes. If the project input changes later, `MatchesProjectInput` is false: the set keeps working but does
  not fit generation of the new size.
- A background keeps only the ready RGBA PNG `files/backgrounds/<id>.png`; sources and rejected files are not copied. Origin:
  `uploaded`, `image-crop` (with the region), `video-frame` (video, timestamp, frame number); blur radius, hash of the source file,
  processing version.
- The order in a set is the order of addition. "Remove" is reversible (`SetItemsRemovedAsync`) and checks the revision of each
  background, not of the set, so a running import causes no conflicts.
- Revisions: the set `Revision` covers name, description and archive; `ContentRevision` — any change of content. An archived set is
  read-only except for restoring and deleting; a set with a running import cannot be archived (`backgrounds.set_importing`).
- For generation — `ListActiveItemsAsync`: a page of active backgrounds with `ContentRevision` and the size of one read snapshot. A
  generation run reads the active backgrounds of its sets in one read snapshot and pins their files with hard links
  ([generation](generation.md#run)).

`IProjectsManager.BackgroundsChanged` reports sets with revisions (null — deleted), backgrounds of explicit operations and
`ItemsChangedIn` — sets with changed content; an import reports at most every 500 ms per set.

## Import

The job `IImportBackgroundsJob` (`backgrounds.import`, CPU, resumable) serves one user action: either files (`Files`, each with an
optional crop) or one video (`Video`: a frame range and one crop). `RequestedCount` stops the run when that many backgrounds are added;
remaining sources are not read.

A frame: decoding with EXIF orientation into sRGB → size check (without a crop exactly `TargetSize`, otherwise
`backgrounds.wrong_size`) or crop check (`backgrounds.crop_out_of_bounds`) → crop → signature → blur → PNG.

- A batch: threads crop sources and compute signatures in parallel; skip decisions are made in source order; threads blur and encode
  the accepted frames in parallel; then exact duplicates are dropped, files are moved into place, rows are inserted in one transaction
  and a checkpoint is saved. The background ID is derived from the job ID and the source number, so a repeat after a crash finds its
  rows.
- Video is read by the shared `VideoFrameReader` (one ffmpeg pass); the video hash is computed once in parallel. A video smaller than
  the set — `backgrounds.video_too_small`, a crop outside the frame — `backgrounds.region_invalid`. Available frames:
  `BackgroundVideoSource.AvailableFrames` = max(0, ⌊(duration − 1 − start) / step⌋ + 1).
- Skip reasons are kept in the result (`ImportBackgroundsResult.Issues`, the first 100); if no source fits, the job ends with
  `backgrounds.no_usable_media`. The import history of a set is `ListRunsAsync`.

### Duplicates and similarity

A signature is the frame before blur, averaged down to 32×32 grey levels (Rec. 709 luma). A frame is similar if the mean difference of
signatures is at most 3/255. With `SkipSimilar` a frame is compared with all backgrounds of the set (removed included) and with frames
taken earlier by this run. An exact duplicate — the same pixels (`PixelHash`) in the set — is always skipped.

The comparison is a hot loop: each frame against all signatures of the set. `SignatureDistance` sums differences with AVX2/SSE2
instructions (`VPSADBW`) in 256-byte blocks with early exit; the scalar loop is the fallback and the reference in checks.

### Blur

SkiaSharp Gaussian blur (sigma = radius, edges clamped) after cropping. The radius of a frame is deterministic: a hash of the seed and
the source index, rounded to 0.01 px; below 0.3 px the frame stays sharp. The default seed is the first 4 bytes of
SHA-256(`projectId:operationId`), so samples and the run of one operation match; the result keeps the chosen seed. The radius is
0–1,000 px (`backgrounds.blur_invalid`).

## Before the run

Short operations for the "add" dialog save nothing:

- `InspectFilesAsync` — headers of the selected files without decoding: size with orientation and fitness (`Exact`, `Larger`,
  `Smaller`, `Unreadable`).
- `PreviewFileAsync` and `PreviewVideoFrameAsync` — a frame for cropping of at most 1,600 px; the file is decoded scaled (JPEG directly
  at 1/2, 1/4, 1/8).
- `PreviewImportAsync` — up to 6 samples spread over the sources (for video — over the first `RequestedCount` frames), with the same
  crops, radii and pixels the run of the same operation produces. Video frames are read by seeking; with `SkipSimilar` a similar frame
  is replaced by the next one.

## Permanent deletion

A content review (the whole set or up to 400 selected backgrounds) and deletion with its revision, as for
[images](images.md#permanent-deletion): rows and (for a set) import history are deleted in a transaction, files and thumbnails in the
background (`FileCleanup`). Only what belongs to this set blocks (providers `BackgroundBlockers`): a running import
(`backgrounds.import_active`) and generation presets when the whole set or all its active backgrounds are deleted
(`generation.preset_reference`).
