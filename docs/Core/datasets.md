# Datasets

`IProject.Datasets` holds immutable versions of ready frames and annotation in YOLO format, build plans and operations on versions. It
works only for an open project (`datasets.project_not_open`). Contracts are in `Interfaces/Projects/Datasets`, the implementation in
`Projects/Datasets`.

```csharp
var plan = await project.Datasets.PlanAsync(specification, operationId);          // "check before building"
var input = await project.Datasets.PrepareBuildAsync(plan.Id, plan.Hash, operationId);
var job = await core.Jobs.EnqueueAsync<IDatasetBuildJob, DatasetBuildInput>(input, project.Id, operationId);
var page = await project.Datasets.ListSamplesAsync(job.Id, DatasetSplit.Train, limit: 100);
```

Datasets read the catalog and images through their managers in one direction; those know nothing about datasets.

## Plan

A specification: name, description, groups, classes, target and negative class variants, roles of objects without a variant and of
unselected classes, empty frames, unfinished generations, seed (uint32), validation (> 0) and test (≥ 0, sum < 1) shares. An invalid
field has its own code with `Details["field"]`.

- **Selection.** Non-archived images of the selected groups; skipped are unreviewed frames, unfinished generations (unless allowed),
  frames with a box or ROI outside [0, 1] (1e-6 tolerance) and frames without target objects unless empty frames are included. Skip
  reasons are counted separately.
- **Object roles.** An archived or deleted class or variant is a negative (counted in a warning); an unselected class and an object
  without a variant follow the roles of the specification, and "refuse" rejects the plan; a variant of a selected class without a role is
  `datasets.variant_role_missing`.
- **Splits.** A group with a split hint goes wholly to its split. Frames of "unassigned" groups are split deterministically: union-find
  components by source family (a generated frame has its own `generated-image:<id>`) and pixelHash, the key is the minimum
  `<family>:<pixelHash>`, `v = uint32BE(sha256("<seed>:<key>")) / 2^32`: test first, then val, otherwise train. A component that joins
  different hints gives a warning; identical pixels in different splits are `datasets.split_collision`, and the build is impossible.
- **Classes.** Selected active classes in catalog order get indexes 0..n−1.

The plan keeps the outcome (content, reasons, warnings, hash) and the source revision, but neither frames nor files. The revision is the
aggregate of the groups and their images, a fingerprint of the catalog and the input size; if it changed, `PrepareBuildAsync` answers
`datasets.plan_stale`.

## Build

The job `datasets.build` (CPU, resumable); the version ID is the job ID.

1. **Preparation**: sources are read again and the selection repeated; another hash — `datasets.plan_stale`. The version row
   (`building`) and the rows of all samples are written in one transaction — from then on the build does not depend on annotation edits.
2. **Files**: windows of samples in parallel. The source file is read once and checked against the import SHA-256; JPEG and PNG without
   EXIF rotation become a hard link (a copy where impossible), others a PNG of the oriented pixels with the same pixelHash. A label is
   `<index> <cx> <cy> <w> <h>` with 8 decimals. The results of a window are written in one transaction, then a checkpoint; a repeated
   window replaces its files and rows.
3. **Publishing**: a check for identical pixels in different splits, `manifest.json` and `data.yaml`, the version `ready` and `verified`
   in one transaction.

Cancellation and failure delete the rows and the version folder; process death keeps them for continuation. When a project opens,
`building` versions without an active job are deleted. While a build runs, images of its groups cannot be deleted
(`datasets.build_active`).

```text
files/datasets/<versionId>/
  images/{train,val,test}/<imageId>.jpg|.png
  labels/{train,val,test}/<imageId>.txt
  data.yaml        path: .; train/val/test; names: {index: "name"}
  manifest.json    classes [{index, classId, name}], splits, samples with hash and geometry
```

## Versions and operations

- A version is immutable except for name, description and archive (revision, `datasets.revision_conflict`). An archived version cannot
  be changed, verified, exported or given to training (`datasets.archived`).
- Samples are the `dataset_samples` table, paged by `(split, image_id)`. A sample file is served by the resource channel, kind
  `datasets` (a nested path from the sample row).
- **Integrity check** — the job `datasets.verify`: all files are read in parallel and checked against SHA-256, every problem is listed;
  the version becomes `verified` or `corrupt`.
- **ZIP export** — the job `datasets.export`: Desktop chooses the path in "Save as"; the archive is written to a temporary file next to it
  and replaces the target; images are stored uncompressed, hashes are checked while reading. A corrupt version is not exported.
  Cancellation and failure leave no file.
- Operations are Jobs jobs with a history per kind; only a finished operation can be removed from it.

## Deletion

`ReviewDeletionAsync` → `DeleteAsync` with the content revision. Blockers are collected by the `DatasetBlockers` registry of the Core
composition: verification and export of this version, materialization for training, a training run on it that is queued or running
(`training.active`), a model evaluation on it (`checks.evaluation_active`). Deletion is two-phase: samples and history in a transaction,
the folder in the background (`FileCleanup`, the folder is queued as a whole). The version row stays as a passport for models
(`GetPassportAsync`: specification, classes, content, manifest hash).

## Training

`GetTrainingSourceAsync` gives the version folder for reading; `MaterializeAsync` creates hard links of images/ and labels/ and copies
of `data.yaml`, `manifest.json` in an empty job folder. Both require a ready, non-archived, intact version with the project input size
and classes 0..n−1 (`datasets.input_size_mismatch`, `datasets.classes_invalid`).

## Events

`IProjectsManager.DatasetsChanged`: versions with new revisions or deleted ones. Progress and results of operations come with
`JobChanged`.
