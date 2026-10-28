# Catalog: variants and classes

The project catalog is what the model learns to recognize. It consists of two child managers of a project: `Variants` (shared
appearance variants of objects) and `Classes` (classes, their variants and source images). Both work only for an open project:
before `IProjectsManager.OpenAsync` calls are rejected with `variants.project_not_open` and `classes.project_not_open`.

```csharp
var project = await core.Projects.OpenAsync(projectId);
var day = await project.Variants.CreateAsync("День", "");
var mug = await project.Classes.CreateAsync("Кружка", "");
var binding = await project.Classes.AddVariantAsync(mug.Id, day.Id);
var workspace = await project.Classes.GetWorkspaceAsync();
```

## Objects

- **Project variant** — a way an object looks, shared by all classes (day, night, mock-up). Generation chooses their shares.
- **Class** — an object to recognize. The class order (`SortOrder`) defines YOLO indexes: when a dataset is built, the selected
  classes get indexes from 0 without gaps in this order. Built datasets and models keep their own class map and are never renumbered.
- **Class variant** (binding) — a project variant attached to a class, once per class. Name and description come from the project
  variant; the binding has its own box colour and archive flag.
- **Class variant image** — the original file unchanged in `files/class-assets/<id>.<ext>` and a row with format, size, alpha
  channel and SHA-256. Thumbnails are served by the [resource channel](../Desktop/resources.md), kind `class-assets`.

Names are trimmed, contain 1–120 characters and are unique case-insensitively within their list including the archive; the comparison
key is `ToLowerInvariant` after NFC in C#, because SQLite `lower()` knows only ASCII. A description is up to 2,000 characters. A
colour is `#rrggbb` or null: then the palette colour by FNV-1a of the binding ID (`VariantColors`).

## Revisions and events

Every object change checks the revision the user saw (`*.revision_conflict`) and increments it. The class order changes only by a full
permutation of all classes, archived included, with the order revision (`classes.order_revision_conflict`); creating and deleting a
class also change the order revision.

Any catalog change increments `CatalogRevision` and raises `IProjectsManager.CatalogChanged`: which objects changed with their new
revisions (null — deleted) and which bindings had image changes. Lists are not sent in the event; the UI rereads the light
`GetWorkspaceAsync` snapshot and image pages by cursor.

## Archive

Archiving affects only the object itself, without cascading. A binding is available if neither it, nor its class, nor its project
variant is archived; images are not added to an unavailable binding, and a new binding is not attached to an archived class or
variant. Restoring a binding requires an active class and project variant.

An archived object is read-only: the name and description of an archived variant or class do not change (`variants.archived`,
`classes.archived`), nor does the colour of an archived binding (`classes.variant_archived`). Archiving, restoring and permanent
deletion are allowed. The colour of a binding that is unavailable only because its class or variant is archived can be changed.

## Permanent deletion

Deletion has two steps: a review of the content and the deletion with its revision.

```csharp
var review = await project.Variants.ReviewDeletionAsync(day.Id);   // counters, blockers, Revision
var result = await project.Variants.DeleteAsync(day.Id, review.Revision);
```

- A project variant deletes its bindings in all classes and their images; a class deletes its bindings and images; a binding
  deletes its images.
- The content revision is a hash of the target and all rows to delete. Deletion recomputes it inside the write transaction; a mismatch
  is `*.deletion_revision_conflict`.
- Blocking reasons are collected by the provider registry `CatalogBlockers`, filled by the Core composition: queued or running jobs of
  the project (`jobs.active`); the current annotation of images that references the class, class variant or project variant
  (`images.annotations`: number of images and some names, see [images](images.md#annotation)); generation presets, archived included
  (`generation.preset_reference`: a binding in a preset, a class through its bindings, a project variant with a share above 0 %; see
  [generation](generation.md#blockers-in-other-sections)).
- Deletion is two-phase (the project's `FileCleanup`, shared by all material kinds). The transaction deletes the rows and queues all
  their files in `file_cleanup` (a material folder, e.g. a dataset version, is one entry with the `.directory` extension); the call does
  not wait for all files — a background pass removes them: several low-priority threads read and remove the queue in parts, each part
  committed separately. Thumbnails in `cache/` are removed with the file, ignoring failures.
- The call waits for the background pass at most 250 ms: an ordinary deletion of a few files finishes in time. `CleanupPending` means
  files are still being removed (many or partly locked). A locked file stays queued; the queue is processed on the next deletion and
  when the project opens, not at application start. If the application exits earlier, deletion continues at the next opening.
- Image IDs are never reused: a new import gets new IDs, so files in the cleanup queue never collide with files of new images.

## Image import

Import is the job `IImportClassAssetsJob` (CPU, resumable) for one user action: a binding and the selected files. Desktop shows the
native dialog and gives the UI only a selection token.

- PNG, JPEG, WebP and BMP are accepted; the format is detected by content. Each file is fully decoded by SkiaSharp at full size: a
  truncated or damaged file is rejected (`classes.asset_invalid`). The size with EXIF orientation and the presence of an alpha
  channel (`hasAlpha` — the file has the channel, not transparent pixels) are taken from the file. There is no pixel size limit.
- Files are processed in batches of 64 by several threads. A file up to 32 MB is read into memory once: SHA-256, decoding and the
  write next to the destination use the same bytes; a larger one is copied as a stream and decoded from the copy. Then the file is
  renamed into place. Rows of a batch are written in one transaction, then a checkpoint is saved.
- Identical content in the same binding is skipped. The image ID is derived from the job ID and the file number, so repeating a batch
  after a crash overwrites the same file and finds its row instead of creating a copy.
- The result (`ImportClassAssetsResult`: total, added, skipped, rejected with reasons) is kept in `JobInfo.Result`, also for a
  cancelled job.
