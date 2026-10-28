using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Hardware;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Training;
using MautVision.Core.Interfaces.Python;
using MautVision.Core.Interfaces.Training;
using MautVision.Desktop.Controllers.Projects.Training.RequestDto;
using MautVision.Desktop.Controllers.Projects.Training.ResponseDto;
using MautVision.Desktop.Controllers.Jobs;
using MautVision.Desktop.Controllers.Jobs.ResponseDto;
using MautVision.Desktop.Resources;
using MautVision.Desktop.Host;

namespace MautVision.Desktop.Controllers.Projects.Training;

/// <summary>
/// Training: global presets, GPUs and the worker runtime, runs of an opened project (a run starts as the job training.run; its state
/// and progress come through job.changed, its live data through training.changed), history, log and model versions.
/// </summary>
internal sealed class TrainingController(IProjectsManager projects, IJobsManager jobs, ITrainingPresetsManager presets, IHardwareManager hardware,
    IPythonRuntime runtime, ResourceHandles resources, FileSelections selections, NativeDialogs dialogs)
{
    public async Task<TrainingPresetResponseDto[]> ListPresetsAsync(ListTrainingPresetsRequestDto request, CancellationToken cancellationToken)
        => (await presets.ListAsync(cancellationToken)).Select(TrainingResponseMapper.Map).ToArray();

    public async Task<TrainingPresetResponseDto> SavePresetAsync(SaveTrainingPresetRequestDto request, CancellationToken cancellationToken)
        => TrainingResponseMapper.Map(await presets.SaveAsync(request.Name, request.Parameters, cancellationToken));

    public async Task<TrainingPresetDeletionResponseDto> DeletePresetAsync(DeleteTrainingPresetRequestDto request, CancellationToken cancellationToken)
    {
        await presets.DeleteAsync(request.PresetId, cancellationToken);
        return new TrainingPresetDeletionResponseDto(request.PresetId);
    }

    public async Task<GpuInventoryResponseDto> ListGpusAsync(ListGpusRequestDto request, CancellationToken cancellationToken)
        => TrainingResponseMapper.Map(await hardware.ListGpusAsync(request.Refresh ?? false, cancellationToken));

    public Task<TrainingRuntimeResponseDto> GetRuntimeAsync(GetTrainingRuntimeRequestDto request, CancellationToken cancellationToken)
    {
        var status = runtime.GetStatus();
        var code = status.Code is null ? null : "training." + status.Code;
        return Task.FromResult(new TrainingRuntimeResponseDto(status.Configured, status.Available, status.Mode, code));
    }

    /// <summary>Checks the run and queues it; returns it at once. A repeated operation returns the same run.</summary>
    public async Task<TrainingRunResponseDto> StartRunAsync(StartTrainingRunRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OperationId);
        var training = await Training(request.ProjectId, cancellationToken);
        var input = await training.PrepareRunAsync(new TrainingRunRequest(request.DatasetVersionId, request.Parameters, request.PresetName), request.OperationId,
            cancellationToken);
        var job = await jobs.EnqueueAsync<ITrainingRunJob, TrainingRunInput>(input, request.ProjectId, request.OperationId, input.Dataset.Name,
            cancellationToken);
        return TrainingResponseMapper.Map(await training.GetRunAsync(job.Id, cancellationToken));
    }

    public async Task<TrainingRunResponseDto> CancelRunAsync(CancelTrainingRunRequestDto request, CancellationToken cancellationToken)
    {
        var training = await Training(request.ProjectId, cancellationToken);
        await training.GetRunAsync(request.RunId, cancellationToken);
        await jobs.CancelAsync(request.ProjectId, request.RunId, cancellationToken);
        return TrainingResponseMapper.Map(await training.GetRunAsync(request.RunId, cancellationToken));
    }

    public async Task<TrainingRunPageResponseDto> ListRunsAsync(ListTrainingRunsRequestDto request, CancellationToken cancellationToken)
    {
        var page = await (await Training(request.ProjectId, cancellationToken)).ListRunsAsync(request.Limit ?? 50, request.Before, cancellationToken);
        return new TrainingRunPageResponseDto(page.Items.Select(run => TrainingResponseMapper.Map(run)).ToArray(), page.Total);
    }

    public async Task<TrainingRunResponseDto> GetRunAsync(GetTrainingRunRequestDto request, CancellationToken cancellationToken)
        => TrainingResponseMapper.Map(await (await Training(request.ProjectId, cancellationToken)).GetRunAsync(request.RunId, cancellationToken));

    public async Task<TrainingEpochPageResponseDto> ListEpochsAsync(ListTrainingEpochsRequestDto request, CancellationToken cancellationToken)
        => TrainingResponseMapper.Map(await (await Training(request.ProjectId, cancellationToken)).ListEpochsAsync(request.RunId, request.After,
            request.Limit ?? 500, cancellationToken));

    public async Task<TrainingLogPageResponseDto> ListLogAsync(ListTrainingLogRequestDto request, CancellationToken cancellationToken)
        => TrainingResponseMapper.Map(await (await Training(request.ProjectId, cancellationToken)).ListLogAsync(request.RunId, request.After,
            request.Limit ?? 200, cancellationToken));

    public async Task<TrainingLogPageResponseDto> TailLogAsync(TailTrainingLogRequestDto request, CancellationToken cancellationToken)
        => TrainingResponseMapper.Map(await (await Training(request.ProjectId, cancellationToken)).TailLogAsync(request.RunId, request.Limit ?? 50, cancellationToken));

    public async Task<ModelVersionResponseDto[]> ListVersionsAsync(ListModelVersionsRequestDto request, CancellationToken cancellationToken)
        => (await (await Training(request.ProjectId, cancellationToken)).ListVersionsAsync(cancellationToken))
            .Select(version => TrainingResponseMapper.Map(request.ProjectId, version, resources)).ToArray();

    public async Task<ModelVersionResponseDto> GetVersionAsync(GetModelVersionRequestDto request, CancellationToken cancellationToken)
        => TrainingResponseMapper.Map(request.ProjectId, await (await Training(request.ProjectId, cancellationToken)).GetVersionAsync(request.VersionId,
            cancellationToken), resources);

    public async Task<ModelVersionResponseDto> SetWorkingVersionAsync(SetWorkingModelVersionRequestDto request, CancellationToken cancellationToken)
        => TrainingResponseMapper.Map(request.ProjectId, await (await Training(request.ProjectId, cancellationToken)).SetWorkingVersionAsync(request.VersionId,
            cancellationToken), resources);

    public async Task<ModelVersionDeletionReviewResponseDto> ReviewVersionDeletionAsync(ReviewModelVersionDeletionRequestDto request,
        CancellationToken cancellationToken)
    {
        var review = await (await Training(request.ProjectId, cancellationToken)).ReviewVersionDeletionAsync(request.VersionId, cancellationToken);
        return new ModelVersionDeletionReviewResponseDto(review.VersionId, review.Number, review.IsWorking, review.Evaluations, review.Bytes,
            review.Blockers.Select(blocker => Catalog.ResponseDto.DeletionBlockerResponseDto.From(blocker)).ToArray());
    }

    public async Task<ModelVersionDeletionResponseDto> DeleteVersionAsync(DeleteModelVersionRequestDto request, CancellationToken cancellationToken)
    {
        var result = await (await Training(request.ProjectId, cancellationToken)).DeleteVersionAsync(request.VersionId, cancellationToken);
        return new ModelVersionDeletionResponseDto(result.VersionId, result.Number, result.Evaluations, result.CleanupPending);
    }

    /// <summary>«Сохранить как» with «model-vN.zip»; overwriting is confirmed in the dialog itself. Null token — the dialog was closed.</summary>
    public async Task<ModelExportSelectionResponseDto> SelectExportDestinationAsync(SelectModelExportRequestDto request, CancellationToken cancellationToken)
    {
        var version = await (await Training(request.ProjectId, cancellationToken)).GetVersionAsync(request.VersionId, cancellationToken);
        var path = await dialogs.PickSaveFile(Strings.DialogExportModel, $"model-v{version.Number}.zip", Strings.FilterZip, cancellationToken);
        if (string.IsNullOrEmpty(path)) return new ModelExportSelectionResponseDto(null, null);
        return new ModelExportSelectionResponseDto(selections.Register([path], FileSelectionKind.SaveFile), global::System.IO.Path.GetFileName(path));
    }

    /// <summary>Starts the ZIP export as the job training.export; its progress and result come through job.changed.</summary>
    public async Task<JobResponseDto> StartExportAsync(StartModelExportRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OperationId);
        var training = await Training(request.ProjectId, cancellationToken);
        var selection = selections.Resolve(request.DestinationToken, request.OperationId);
        if (selection is not { Kind: FileSelectionKind.SaveFile, Paths.Count: 1 })
            throw new CoreException("training.export_selection_expired", "The export destination is unknown, expired or belongs to another operation.");
        var input = await training.PrepareExportAsync(request.VersionId, selection.Paths[0], request.OperationId, cancellationToken);
        return JobResponseMapper.Map(await jobs.EnqueueAsync<IModelExportJob, ModelExportInput>(input, request.ProjectId, request.OperationId,
            JobLabels.Version(input.VersionNumber), cancellationToken));
    }

    private async Task<ITrainingManager> Training(string projectId, CancellationToken cancellationToken)
        => (await projects.GetAsync(projectId, cancellationToken)).Training;
}
