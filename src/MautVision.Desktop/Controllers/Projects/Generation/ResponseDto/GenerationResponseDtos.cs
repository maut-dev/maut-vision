using MautVision.Desktop.Controllers.Jobs.ResponseDto;
using MautVision.Desktop.Controllers.Projects.Generation.RequestDto;
using MautVision.Desktop.Controllers.Projects.Images.ResponseDto;

namespace MautVision.Desktop.Controllers.Projects.Generation.ResponseDto;

/// <summary>
/// Generation preset: form settings, Revision — for editing, archiving and running; ArchivedAt — "Archived". Deleted and archived
/// sets, bindings and variants are marked by the UI from its own lists.
/// </summary>
public sealed record GenerationRecipeResponseDto(
    Guid Id,
    string Name,
    string Description,
    int Width,
    int Height,
    Guid[] BackgroundSetIds,
    Guid[] ClassVariantIds,
    GenerationVariantWeightDto[] VariantWeights,
    int ObjectsPerImage,
    double EmptyProbability,
    double Scale,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt);

/// <summary>
/// Run: Job — snapshot of the generation.run job (state, processed/total progress — "P / R images", error with text, result);
/// Recipe — a copy of the preset ("Settings of this run"); ProducedCount — published; MissingObjectFrames — frames with missing
/// objects; OutputGroupId — the result group ("Open images"; null until the run is prepared); OutputGroupName — its name.
/// </summary>
public sealed record GenerationRunResponseDto(
    JobResponseDto Job,
    GenerationRecipeResponseDto Recipe,
    uint Seed,
    int RequestedCount,
    int ProducedCount,
    int MissingObjectFrames,
    Guid? OutputGroupId,
    string OutputGroupName);

/// <summary>Page of runs, newest first; Total — "Runs (N)".</summary>
public sealed record GenerationRunPageResponseDto(GenerationRunResponseDto[] Items, long Total);

public sealed record GenerationRecipeDeletionResponseDto(Guid RecipeId);

/// <summary>The run is deleted; the images of the result group are kept.</summary>
public sealed record GenerationRunDeletionResponseDto(Guid RunId);

/// <summary>"Configure repeat": settings for the new preset form (name " · repeat"), the run seed and count, a notice for the form.</summary>
public sealed record GenerationRepeatDraftResponseDto(GenerationRecipeFormDto Recipe, uint Seed, int RequestedCount, Guid SourceRunId);

/// <summary>Frame object: class, binding (its box color comes from the catalog) and box as fractions of the frame.</summary>
public sealed record GenerationSampleObjectDto(Guid ClassId, Guid ClassVariantId, ImageBoxResponseDto Box);

/// <summary>
/// "Sample frame" frame: ImageUrl — a temporary PNG resource (valid for 10 minutes for this page), Width/Height — frame size,
/// BackgroundId — the background, Objects — boxes, MissingObjects — how many objects did not fit.
/// </summary>
public sealed record GenerationSampleResponseDto(int SampleIndex, string ImageUrl, int Width, int Height, Guid BackgroundId, GenerationSampleObjectDto[] Objects,
    int MissingObjects);

public sealed record GenerationSamplesResponseDto(GenerationSampleResponseDto[] Samples);

/// <summary>Changed or deleted (Revision = null) preset.</summary>
public sealed record GenerationEntityChangeDto(Guid Id, long? Revision);

/// <summary>
/// The generation.changed event: Recipes — presets with new revisions or deleted ones; Runs — runs whose data changed
/// beyond the job state (prepared — the result group appeared; deleted): reread them with getRun. State and progress — job.changed.
/// </summary>
public sealed record GenerationChangedEventDto(string ProjectId, GenerationEntityChangeDto[] Recipes, Guid[] Runs);
