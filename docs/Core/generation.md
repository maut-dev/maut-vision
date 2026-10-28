# Generation

`IProject.Generation` holds generation presets and runs: objects cut from class images are placed onto backgrounds, and the frames are
annotated right away. It works only for an open project (`generation.project_not_open`). Contracts are in
`Interfaces/Projects/Generation`, the implementation in `Projects/Generation`.

```csharp
var recipe = await project.Generation.CreateRecipeAsync(new GenerationRecipeInput("Улица", "", 640, 640,
    [setId], [mugDayId], [new GenerationVariantWeight(dayId, 100)], ObjectsPerImage: 3, EmptyProbability: 0.1, Scale: 0.5));
var input = await project.Generation.PrepareRunAsync(recipe.Id, recipe.Revision, seed: 42, count: 500, operationId);
var job = await core.Jobs.EnqueueAsync<IGenerationRunJob, GenerationRunInput>(input, project.Id, operationId);
```

## Presets

- A preset has a name of 1–120 characters, a description up to 4,000, a frame size (the project input, a multiple of 32), background
  sets, bindings (class variants), shares of project variants (0–100 %, sum 100 ± 1e-6), objects per frame (integer ≥ 0), the share of
  empty frames (0..1) and a scale (> 0). There are no upper limits. An invalid field — `generation.invalid_recipe` with
  `Details["field"]`.
- Preset bindings belong only to variants with a share above 0. References must exist when saving; the run checks the archive.
- The revision grows with every change (`generation.revision_conflict`). An archived preset is read-only (`generation.recipe_archived`)
  and cannot run. An unfinished run of a preset blocks its deletion (`generation.deletion_blocked`); runs keep a copy of the preset.

## Run

A run is the job `generation.run` (CPU, resumable); the run ID is the job ID. `PrepareRunAsync` checks the preset, its revision, the
frame size against the project input and the sources without decoding, and returns the job input with a copy of the preset; repeating
the same operation returns its input, and the operation of a deleted run is `generation.deleted_operation`.

1. **Preparation** (once): source checks, a manifest — ordered lists of bindings with images and of active backgrounds; a snapshot —
   hard links of only these files in `files/generation-runs/<runId>/` (a copy if a link is impossible); a result group `generated` in
   the `producing` state; the manifest and the group are saved in `generation_runs`.
2. **Frames**: frame windows are rendered in parallel (threads follow the CPU slot rule); the next window renders while the previous one
   is published. Publishing is strictly ordered: files are moved into place, image rows with annotation go in one transaction, then the
   checkpoint `producedCount` (a contiguous prefix).
3. **Result**: the group becomes `completed`; cancellation and failure keep what was published, and the group becomes `partial`. The
   snapshot is removed in any outcome except process death: then the run continues from the checkpoint after the project opens, with
   the same frames, because image IDs are derived from the run ID and the frame number.

The result is `GenerationRunResult` in `JobInfo.Result`: requested, published, frames with missing objects, group, seed. Only a finished
run can be deleted (`generation.run_active`); its images stay. When a project opens, `producing` groups and run snapshots that are
neither queued nor running are finished as `partial` and released.

Images: `generated-<i>.png`, lineage `generated` {runId, sampleIndex, recipeRevision}, family `generation:<runId>`, annotation
revision 1 `reviewed` (an empty frame has no objects), no ROI. The group a run fills is read-only (`images.group_busy`).

## Algorithm

A frame depends only on the seed, the frame number and the manifest, so it is identical with any number of threads and after
continuation.

- The frame RNG is mulberry32 seeded with `seed ^ imul(i + 1, 0x9e3779b9)` (`FrameRandom`). Order of draws: whether the frame is empty;
  per object — the project variant by shares (the first weight > 0 where `r·100` is below the running sum), the binding, the image, the
  position.
- Size: `ratio = min(W·scale/w, H·scale/h, W/w, H/h, 1)`, side `max(1, round(side·ratio))` (rounding like `Math.round`).
- Position: up to 100 random attempts without overlaps (touching is allowed), then a search along the edges of placed objects. An object
  without room is skipped and counted in the result.
- Background: `(seed + imul(i + 1, 2654435761)) mod 2^32 mod N` over backgrounds in the order of the preset sets and of addition.
- A sprite is made once per image: decoding with orientation into sRGB, a check for transparent pixels (`generation.unsupported_asset`;
  an image without an alpha channel is already rejected by the source check), a crop by alpha > 0, one high-quality downscale to the run
  size (by halves, then Mitchell). A frame is a copy of the background with sprites over it (SkiaSharp, source-over) and one PNG encode
  (Paeth, zlib 1).

## Sample frames

`PreviewSamplesAsync` renders up to 4 first frames of the form settings with the run seed: the same pixels and boxes the run produces,
without saving (PNG bytes; Desktop serves them as transient resources). Checks are those of the preset and the run.

## Blockers in other sections

The providers are registered by the Core composition:

- catalog (`generation.preset_reference`, preset names as examples): a binding in a preset, a class through its bindings, a project
  variant with a share > 0 or with its bindings; archived presets also block, a 0 % share does not;
- backgrounds (`generation.preset_reference`): deleting a set used by a preset or all its active backgrounds;
- images (`images.generation_active`, the group name as an example): a group in the `producing` state.

## Events

`IProjectsManager.GenerationChanged`: presets with revisions (null — deleted) and runs whose data changed besides the job state
(prepared, deleted). State and progress come with `JobChanged`; the result group and images with `ImagesChanged`.
