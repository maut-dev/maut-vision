using MautVision.Core.Interfaces.Jobs;

namespace MautVision.Core.Interfaces.Projects.Generation;

/// <summary>
/// Run input: a copy of the preset (with revision), seed (uint32), frame count and the name of the result group. Prepared by
/// <see cref="IGenerationManager.PrepareRunAsync"/>; the run ID is the job ID.
/// </summary>
public sealed record GenerationRunInput(GenerationRecipe Recipe, uint Seed, int RequestedCount, string OutputGroupName);

/// <summary>
/// Generation run (CPU, resumable). Preparation: checks of the frame size (<c>generation.canvas_mismatch</c>), of the background
/// set sizes (<c>generation.background_size_mismatch</c>), availability of variants and bindings (<c>generation.variant_unavailable</c>),
/// images (<c>generation.variant_without_asset</c>, <c>generation.unsupported_asset</c>), backgrounds
/// (<c>generation.backgrounds_empty</c>); a snapshot of the needed files; the result group in state producing. Then frames are rendered
/// in windows in parallel and published strictly in order in batches with a checkpoint (producedCount). After an application restart the run
/// continues from the checkpoint with the same frames. Cancellation and errors keep what was published, the group becomes partial; a successful
/// run — completed. The result is <see cref="GenerationRunResult"/>.
/// </summary>
[JobKind("generation.run")]
public interface IGenerationRunJob : IJobDefinition<GenerationRunInput>
{
}
