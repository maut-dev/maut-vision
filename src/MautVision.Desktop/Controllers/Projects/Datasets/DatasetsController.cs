using System.IO;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Datasets;
using MautVision.Desktop.Controllers.Projects.Datasets.RequestDto;
using MautVision.Desktop.Controllers.Projects.Datasets.ResponseDto;
using MautVision.Desktop.Resources;

namespace MautVision.Desktop.Controllers.Projects.Datasets;

/// <summary>
/// Datasets of an opened project: versions, plans, samples (resource URLs of their files), deletion and the operations build,
/// integrity check and ZIP export, which start as jobs and report through job.changed. The export destination is chosen in a
/// native «Сохранить как» dialog; the page gets only a selection token.
/// </summary>
internal sealed class DatasetsController(IProjectsManager projects, IJobsManager jobs, ResourceHandles resources, FileSelections selections, NativeDialogs dialogs,
    Func<string, string> messageFor)
{
    public async Task<DatasetVersionResponseDto[]> ListAsync(ListDatasetsRequestDto request, CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(request.ProjectId, cancellationToken);
        var versions = await project.Datasets.ListAsync(request.IncludeArchived ?? true, cancellationToken);
        return versions.Select(version => DatasetsResponseMapper.Map(version, project.InputSize)).ToArray();
    }

    public async Task<DatasetVersionResponseDto> GetAsync(GetDatasetRequestDto request, CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(request.ProjectId, cancellationToken);
        return DatasetsResponseMapper.Map(await project.Datasets.GetAsync(request.VersionId, cancellationToken), project.InputSize);
    }

    public async Task<DatasetVersionResponseDto> GetPassportAsync(GetDatasetPassportRequestDto request, CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(request.ProjectId, cancellationToken);
        return DatasetsResponseMapper.Map(await project.Datasets.GetPassportAsync(request.VersionId, cancellationToken), project.InputSize);
    }

    public async Task<DatasetVersionResponseDto> UpdateAsync(UpdateDatasetRequestDto request, CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(request.ProjectId, cancellationToken);
        return DatasetsResponseMapper.Map(await project.Datasets.UpdateAsync(request.VersionId, request.Name, request.Description, request.Revision,
            cancellationToken), project.InputSize);
    }

    public async Task<DatasetVersionResponseDto> SetArchivedAsync(SetDatasetArchivedRequestDto request, CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(request.ProjectId, cancellationToken);
        return DatasetsResponseMapper.Map(await project.Datasets.SetArchivedAsync(request.VersionId, request.Archived, request.Revision, cancellationToken),
            project.InputSize);
    }

    public async Task<DatasetPlanResponseDto> PlanAsync(PlanDatasetRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OperationId);
        var specification = DatasetsResponseMapper.Map(request.Specification);
        return DatasetsResponseMapper.Map(await (await Datasets(request.ProjectId, cancellationToken)).PlanAsync(specification, request.OperationId, cancellationToken));
    }

    public async Task<DatasetPlanResponseDto> GetPlanAsync(GetDatasetPlanRequestDto request, CancellationToken cancellationToken)
        => DatasetsResponseMapper.Map(await (await Datasets(request.ProjectId, cancellationToken)).GetPlanAsync(request.PlanId, cancellationToken));

    /// <summary>Checks the plan and starts the build; returns the operation at once with the queued job. A repeated operation returns the same build.</summary>
    public async Task<DatasetOperationResponseDto> StartBuildAsync(StartDatasetBuildRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OperationId);
        var datasets = await Datasets(request.ProjectId, cancellationToken);
        var input = await datasets.PrepareBuildAsync(request.PlanId, request.PlanHash, request.OperationId, cancellationToken);
        var job = await jobs.EnqueueAsync<IDatasetBuildJob, DatasetBuildInput>(input, request.ProjectId, request.OperationId, input.Specification.Name,
            cancellationToken);
        return DatasetsResponseMapper.Map(await datasets.GetOperationAsync(job.Id, cancellationToken), messageFor);
    }

    public async Task<DatasetOperationResponseDto> StartVerifyAsync(StartDatasetVerifyRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OperationId);
        var datasets = await Datasets(request.ProjectId, cancellationToken);
        var input = await datasets.PrepareVerifyAsync(request.VersionId, request.OperationId, cancellationToken);
        var job = await jobs.EnqueueAsync<IDatasetVerifyJob, DatasetVerifyInput>(input, request.ProjectId, request.OperationId, input.VersionName,
            cancellationToken);
        return DatasetsResponseMapper.Map(await datasets.GetOperationAsync(job.Id, cancellationToken), messageFor);
    }

    /// <summary>«Сохранить как» with «&lt;name&gt;.zip»; overwriting is confirmed in the dialog itself. Null token — the dialog was closed.</summary>
    public async Task<DatasetExportSelectionResponseDto> SelectExportDestinationAsync(SelectDatasetExportRequestDto request, CancellationToken cancellationToken)
    {
        var version = await (await Datasets(request.ProjectId, cancellationToken)).GetAsync(request.VersionId, cancellationToken);
        var path = await dialogs.PickSaveFile("Экспорт датасета в ZIP", DefaultFileName(version.Name), "ZIP-архив", cancellationToken);
        if (string.IsNullOrEmpty(path)) return new DatasetExportSelectionResponseDto(null, null);
        return new DatasetExportSelectionResponseDto(selections.Register([path], FileSelectionKind.SaveFile), Path.GetFileName(path));
    }

    public async Task<DatasetOperationResponseDto> StartExportAsync(StartDatasetExportRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OperationId);
        var datasets = await Datasets(request.ProjectId, cancellationToken);
        var selection = selections.Resolve(request.DestinationToken, request.OperationId);
        if (selection is not { Kind: FileSelectionKind.SaveFile, Paths.Count: 1 })
            throw new CoreException("datasets.export_selection_expired", "The export destination is unknown, expired or belongs to another operation.");
        var input = await datasets.PrepareExportAsync(request.VersionId, selection.Paths[0], request.OperationId, cancellationToken);
        var job = await jobs.EnqueueAsync<IDatasetExportJob, DatasetExportInput>(input, request.ProjectId, request.OperationId, input.VersionName,
            cancellationToken);
        return DatasetsResponseMapper.Map(await datasets.GetOperationAsync(job.Id, cancellationToken), messageFor);
    }

    public async Task<DatasetOperationPageResponseDto> ListOperationsAsync(ListDatasetOperationsRequestDto request, CancellationToken cancellationToken)
    {
        var page = await (await Datasets(request.ProjectId, cancellationToken)).ListOperationsAsync(DatasetsResponseMapper.ParseKind(request.Kind),
            request.Limit ?? 50, request.Before, cancellationToken);
        return new DatasetOperationPageResponseDto(page.Items.Select(item => DatasetsResponseMapper.Map(item, messageFor)).ToArray(), page.Total);
    }

    public async Task<DatasetOperationResponseDto> GetOperationAsync(GetDatasetOperationRequestDto request, CancellationToken cancellationToken)
        => DatasetsResponseMapper.Map(await (await Datasets(request.ProjectId, cancellationToken)).GetOperationAsync(request.OperationId, cancellationToken),
            messageFor);

    public async Task<DatasetOperationDeletionResponseDto> DeleteOperationAsync(DeleteDatasetOperationRequestDto request, CancellationToken cancellationToken)
    {
        await (await Datasets(request.ProjectId, cancellationToken)).DeleteOperationAsync(request.OperationId, cancellationToken);
        return new DatasetOperationDeletionResponseDto(request.OperationId);
    }

    public async Task<DatasetSamplePageResponseDto> ListSamplesAsync(ListDatasetSamplesRequestDto request, CancellationToken cancellationToken)
    {
        var page = await (await Datasets(request.ProjectId, cancellationToken)).ListSamplesAsync(request.VersionId, DatasetsResponseMapper.ParseSplit(request.Split),
            request.Limit ?? 100, request.After, cancellationToken);
        return new DatasetSamplePageResponseDto(page.Items.Select(sample => DatasetsResponseMapper.Map(request.ProjectId, sample, resources)).ToArray(), page.NextCursor);
    }

    public async Task<DatasetSampleResponseDto> GetSampleAsync(GetDatasetSampleRequestDto request, CancellationToken cancellationToken)
        => DatasetsResponseMapper.Map(request.ProjectId, await (await Datasets(request.ProjectId, cancellationToken)).GetSampleAsync(request.VersionId,
            request.SampleId, cancellationToken), resources);

    public async Task<DatasetDeletionReviewResponseDto> ReviewDeletionAsync(ReviewDatasetDeletionRequestDto request, CancellationToken cancellationToken)
        => DatasetsResponseMapper.Map(await (await Datasets(request.ProjectId, cancellationToken)).ReviewDeletionAsync(request.VersionId, cancellationToken));

    public async Task<DatasetDeletionResultResponseDto> DeleteAsync(DeleteDatasetRequestDto request, CancellationToken cancellationToken)
    {
        var result = await (await Datasets(request.ProjectId, cancellationToken)).DeleteAsync(request.VersionId, request.Revision, cancellationToken);
        return new DatasetDeletionResultResponseDto(result.VersionId, result.Samples, result.Operations, result.CleanupPending);
    }

    /// <summary>«&lt;name&gt;.zip» with characters a file name cannot hold replaced; an empty result becomes «dataset.zip».</summary>
    internal static string DefaultFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim().TrimEnd('.');
        return (cleaned.Length == 0 ? "dataset" : cleaned) + ".zip";
    }

    private async Task<IDatasetsManager> Datasets(string projectId, CancellationToken cancellationToken)
        => (await projects.GetAsync(projectId, cancellationToken)).Datasets;
}
