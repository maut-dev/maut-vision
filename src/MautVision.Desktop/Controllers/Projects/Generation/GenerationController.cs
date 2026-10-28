using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Generation;
using MautVision.Desktop.Controllers.Projects.Generation.RequestDto;
using MautVision.Desktop.Controllers.Projects.Generation.ResponseDto;
using MautVision.Desktop.Resources;

namespace MautVision.Desktop.Controllers.Projects.Generation;

/// <summary>
/// Generation presets and runs of an opened project. A run starts as the job generation.run (its snapshot, progress and result
/// come through job.changed); samples are transient resources held in memory for the page, never project files.
/// </summary>
internal sealed class GenerationController(IProjectsManager projects, IJobsManager jobs, ResourceHandles resources)
{
    public async Task<GenerationRecipeResponseDto[]> ListRecipesAsync(ListGenerationRecipesRequestDto request, CancellationToken cancellationToken)
        => (await (await Generation(request.ProjectId, cancellationToken)).ListRecipesAsync(cancellationToken)).Select(GenerationResponseMapper.Map).ToArray();

    public async Task<GenerationRecipeResponseDto> GetRecipeAsync(GetGenerationRecipeRequestDto request, CancellationToken cancellationToken)
        => GenerationResponseMapper.Map(await (await Generation(request.ProjectId, cancellationToken)).GetRecipeAsync(request.RecipeId, cancellationToken));

    public async Task<GenerationRecipeResponseDto> CreateRecipeAsync(CreateGenerationRecipeRequestDto request, CancellationToken cancellationToken)
        => GenerationResponseMapper.Map(await (await Generation(request.ProjectId, cancellationToken)).CreateRecipeAsync(
            GenerationResponseMapper.Map(request.Recipe), cancellationToken));

    public async Task<GenerationRecipeResponseDto> UpdateRecipeAsync(UpdateGenerationRecipeRequestDto request, CancellationToken cancellationToken)
        => GenerationResponseMapper.Map(await (await Generation(request.ProjectId, cancellationToken)).UpdateRecipeAsync(request.RecipeId,
            GenerationResponseMapper.Map(request.Recipe), request.Revision, cancellationToken));

    public async Task<GenerationRecipeResponseDto> SetRecipeArchivedAsync(SetGenerationRecipeArchivedRequestDto request, CancellationToken cancellationToken)
        => GenerationResponseMapper.Map(await (await Generation(request.ProjectId, cancellationToken)).SetRecipeArchivedAsync(request.RecipeId, request.Archived,
            request.Revision, cancellationToken));

    public async Task<GenerationRecipeDeletionResponseDto> DeleteRecipeAsync(DeleteGenerationRecipeRequestDto request, CancellationToken cancellationToken)
    {
        await (await Generation(request.ProjectId, cancellationToken)).DeleteRecipeAsync(request.RecipeId, request.Revision, cancellationToken);
        return new GenerationRecipeDeletionResponseDto(request.RecipeId);
    }

    public async Task<GenerationSamplesResponseDto> PreviewSamplesAsync(PreviewGenerationSamplesRequestDto request, CancellationToken cancellationToken)
    {
        var samples = await (await Generation(request.ProjectId, cancellationToken)).PreviewSamplesAsync(GenerationResponseMapper.Map(request.Recipe),
            GenerationResponseMapper.Seed(request.Seed), request.Count ?? 4, cancellationToken);
        return new GenerationSamplesResponseDto(samples.Select(sample => new GenerationSampleResponseDto(sample.SampleIndex,
            resources.IssueTransient(request.ProjectId, sample.Png, "image/png"), sample.Width, sample.Height, sample.BackgroundId,
            sample.Objects.Select(GenerationResponseMapper.Map).ToArray(), sample.MissingObjects)).ToArray());
    }

    public async Task<GenerationRunPageResponseDto> ListRunsAsync(ListGenerationRunsRequestDto request, CancellationToken cancellationToken)
    {
        var page = await (await Generation(request.ProjectId, cancellationToken)).ListRunsAsync(request.Limit ?? 50, request.Before, cancellationToken);
        return new GenerationRunPageResponseDto(page.Items.Select(run => GenerationResponseMapper.Map(run)).ToArray(), page.Total);
    }

    public async Task<GenerationRunResponseDto> GetRunAsync(GetGenerationRunRequestDto request, CancellationToken cancellationToken)
        => GenerationResponseMapper.Map(await (await Generation(request.ProjectId, cancellationToken)).GetRunAsync(request.RunId, cancellationToken));

    /// <summary>Checks and starts the run; returns it at once, with the queued job. A repeated operation returns the same run.</summary>
    public async Task<GenerationRunResponseDto> StartAsync(StartGenerationRunRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OperationId);
        var generation = await Generation(request.ProjectId, cancellationToken);
        var input = await generation.PrepareRunAsync(request.RecipeId, request.RecipeRevision, GenerationResponseMapper.Seed(request.Seed), request.Count,
            request.OperationId, cancellationToken);
        var job = await jobs.EnqueueAsync<IGenerationRunJob, GenerationRunInput>(input, request.ProjectId, request.OperationId, input.Recipe.Name,
            cancellationToken);
        return GenerationResponseMapper.Map(await generation.GetRunAsync(job.Id, cancellationToken));
    }

    public async Task<GenerationRunDeletionResponseDto> DeleteRunAsync(DeleteGenerationRunRequestDto request, CancellationToken cancellationToken)
    {
        await (await Generation(request.ProjectId, cancellationToken)).DeleteRunAsync(request.RunId, cancellationToken);
        return new GenerationRunDeletionResponseDto(request.RunId);
    }

    public async Task<GenerationRepeatDraftResponseDto> GetRepeatDraftAsync(GetGenerationRepeatDraftRequestDto request, CancellationToken cancellationToken)
    {
        var draft = await (await Generation(request.ProjectId, cancellationToken)).GetRepeatDraftAsync(request.RunId, cancellationToken);
        return new GenerationRepeatDraftResponseDto(GenerationResponseMapper.Map(draft.Recipe), draft.Seed, draft.RequestedCount, draft.SourceRunId);
    }

    private async Task<IGenerationManager> Generation(string projectId, CancellationToken cancellationToken)
        => (await projects.GetAsync(projectId, cancellationToken)).Generation;
}
