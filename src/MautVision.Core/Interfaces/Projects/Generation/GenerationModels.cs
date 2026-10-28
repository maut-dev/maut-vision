using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects.Images;

namespace MautVision.Core.Interfaces.Projects.Generation;

/// <summary>Share of a project variant in frames: 0–100 %, the sum of the preset shares is 100 % (± 1e-6).</summary>
public sealed record GenerationVariantWeight(Guid VariantId, double Percentage);

/// <summary>
/// Preset settings from the form. Width/Height — frame size (usually the project input; multiples of 32). BackgroundSetIds — background sets
/// in selection order; ClassVariantIds — bindings (class variants) whose project variant has a share above 0; VariantWeights —
/// shares of project variants (the order drives variant selection). ObjectsPerImage — integer ≥ 0, EmptyProbability — 0..1,
/// Scale — above 0, the largest share of the frame side (above 1 acts as 1).
/// </summary>
public sealed record GenerationRecipeInput(
    string Name,
    string Description,
    int Width,
    int Height,
    IReadOnlyList<Guid> BackgroundSetIds,
    IReadOnlyList<Guid> ClassVariantIds,
    IReadOnlyList<GenerationVariantWeight> VariantWeights,
    int ObjectsPerImage,
    double EmptyProbability,
    double Scale);

/// <summary>
/// Generation preset. Revision grows with every saved change (edit, archive); a run keeps a copy of the preset with its revision.
/// An archived preset is read-only, except for restoring and deletion.
/// </summary>
public sealed record GenerationRecipe(
    Guid Id,
    string Name,
    string Description,
    int Width,
    int Height,
    IReadOnlyList<Guid> BackgroundSetIds,
    IReadOnlyList<Guid> ClassVariantIds,
    IReadOnlyList<GenerationVariantWeight> VariantWeights,
    int ObjectsPerImage,
    double EmptyProbability,
    double Scale,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt)
{
    /// <summary>Preset settings in form shape.</summary>
    public GenerationRecipeInput ToInput() => new(Name, Description, Width, Height, BackgroundSetIds, ClassVariantIds, VariantWeights,
        ObjectsPerImage, EmptyProbability, Scale);
}

/// <summary>
/// Generation run — the job <see cref="IGenerationRunJob"/>. Job — its snapshot (state, progress: Processed — published,
/// Total — requested; error; result <see cref="GenerationRunResult"/>). Recipe — a copy of the preset at run time.
/// ProducedCount — the contiguous prefix of published frames; OutputGroupId — the result group (null until the run is prepared).
/// </summary>
public sealed record GenerationRun(
    JobInfo Job,
    GenerationRecipe Recipe,
    uint Seed,
    int RequestedCount,
    int ProducedCount,
    int MissingObjectFrames,
    Guid? OutputGroupId,
    string OutputGroupName);

/// <summary>Page of runs, newest first; Total — all project runs.</summary>
public sealed record GenerationRunPage(IReadOnlyList<GenerationRun> Items, long Total);

/// <summary>
/// Run result (in <see cref="JobInfo.Result"/>, also after cancellation and failure): RequestedCount, ProducedCount — published,
/// MissingObjectFrames — frames where some objects found no place ("Frames with missing objects: N"),
/// OutputGroupId — the result group, Seed.
/// </summary>
public sealed record GenerationRunResult(int RequestedCount, int ProducedCount, int MissingObjectFrames, Guid? OutputGroupId, uint Seed);

/// <summary>"Configure repeat": the run settings for the form (the name is the preset name unchanged; the UI adds " · repeat" in the user's language), its seed and count.</summary>
public sealed record GenerationRepeatDraft(GenerationRecipeInput Recipe, uint Seed, int RequestedCount, Guid SourceRunId);

/// <summary>Frame object: class, binding (class variant) and box as fractions of the frame.</summary>
public sealed record GenerationSampleObject(Guid ClassId, Guid ClassVariantId, ImageBox Box);

/// <summary>
/// "Sample frame": a PNG with the same pixels that a run with this seed produces for SampleIndex, background BackgroundId, objects with boxes and
/// MissingObjects — how many objects did not fit.
/// </summary>
public sealed record GenerationSample(int SampleIndex, Guid BackgroundId, IReadOnlyList<GenerationSampleObject> Objects, int MissingObjects,
    byte[] Png, int Width, int Height);

/// <summary>Changed or deleted (Revision = null) preset.</summary>
public sealed record GenerationEntityChange(Guid Id, long? Revision);

/// <summary>
/// What changed in the project generation: Recipes — presets with new revisions or deleted ones; Runs — runs whose
/// data changed beyond the job state (prepared — the result group appeared; deleted). State and progress — the JobChanged event.
/// </summary>
public sealed class GenerationChangedEventArgs(string projectId, IReadOnlyList<GenerationEntityChange> recipes, IReadOnlyList<Guid> runs) : EventArgs
{
    public string ProjectId { get; } = projectId;
    public IReadOnlyList<GenerationEntityChange> Recipes { get; } = recipes;
    public IReadOnlyList<Guid> Runs { get; } = runs;
}
