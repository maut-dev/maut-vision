namespace MautVision.Core.Interfaces.Projects.Generation;

/// <summary>
/// Project generation: presets (rules for placing objects on backgrounds) and runs. Works only for an open project
/// (<c>generation.project_not_open</c>). Preset changes check the revision (<c>generation.revision_conflict</c>) and are reported
/// by the event <see cref="IProjectsManager.GenerationChanged"/>; the state of runs — by the JobChanged event of the job
/// <see cref="IGenerationRunJob"/>. A missing preset or run — <c>generation.unavailable</c>.
/// </summary>
public interface IGenerationManager
{
    /// <summary>All presets, including archived ones, by creation time.</summary>
    Task<IReadOnlyList<GenerationRecipe>> ListRecipesAsync(CancellationToken cancellationToken = default);

    Task<GenerationRecipe> GetRecipeAsync(Guid recipeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// A new preset. Invalid settings — <c>generation.invalid_recipe</c> with the field (Details["field"]: name, description, canvas,
    /// backgroundSetIds, classVariantIds, variantWeights, objectsPerImage, emptyProbability, scale). Background sets, bindings and
    /// variants must exist (they may be archived: the run checks that).
    /// </summary>
    Task<GenerationRecipe> CreateRecipeAsync(GenerationRecipeInput input, CancellationToken cancellationToken = default);

    /// <summary>Changes a preset with the revision the user saw; an archived one — <c>generation.recipe_archived</c>.</summary>
    Task<GenerationRecipe> UpdateRecipeAsync(Guid recipeId, GenerationRecipeInput input, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>"Archive" / "Restore"; repeating with the same value changes nothing.</summary>
    Task<GenerationRecipe> SetRecipeArchivedAsync(Guid recipeId, bool archived, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a preset (also an archived one). An unfinished run of this preset blocks the deletion (<c>generation.deletion_blocked</c>);
    /// runs keep a copy of the preset and remain.
    /// </summary>
    Task DeleteRecipeAsync(Guid recipeId, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>Project runs, newest first (limit 1–200, before — ID of the last run of the previous page).</summary>
    Task<GenerationRunPage> ListRunsAsync(int limit = 50, Guid? before = null, CancellationToken cancellationToken = default);

    Task<GenerationRun> GetRunAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a run and prepares the input of the job <see cref="IGenerationRunJob"/>; the job is enqueued by ICore.Jobs.EnqueueAsync with the same
    /// operationId. Checks: the preset exists and is not archived (<c>generation.unavailable</c>), revision (<c>generation.revision_conflict</c>),
    /// count ≥ 1 (<c>generation.count_invalid</c>), the frame size equals the project input (<c>generation.canvas_mismatch</c>),
    /// sources (as for the run, without decoding), operationId of a deleted run (<c>generation.deleted_operation</c>).
    /// If a run of this operation already exists, returns its input (a repeated request).
    /// </summary>
    Task<GenerationRunInput> PrepareRunAsync(Guid recipeId, long expectedRevision, uint seed, int count, string operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a finished run (<c>generation.run_active</c> for an unfinished one); the images of the result group are kept.
    /// The same operationId no longer starts a generation.
    /// </summary>
    Task DeleteRunAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>"Configure repeat": the run settings for the new preset form, seed and count.</summary>
    Task<GenerationRepeatDraft> GetRepeatDraftAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// "Sample frame": up to 4 frames (sampleIndex 0..count−1) of the form settings with this seed — the same pixels and boxes a run produces,
    /// without saving. Checks are the same as for a preset and a run (including <c>generation.unsupported_asset</c>).
    /// </summary>
    Task<IReadOnlyList<GenerationSample>> PreviewSamplesAsync(GenerationRecipeInput input, uint seed, int count = 4,
        CancellationToken cancellationToken = default);
}
