using System.Text.Json;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects.Backgrounds;
using MautVision.Core.Interfaces.Projects.Datasets;
using MautVision.Core.Interfaces.Projects.Generation;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Interfaces.Projects.Training;
using MautVision.Desktop.Controllers.Projects.Training.ResponseDto;
using MautVision.Desktop.Controllers.Projects.Backgrounds;
using MautVision.Desktop.Controllers.Projects.Datasets;
using MautVision.Desktop.Controllers.Jobs.ResponseDto;

namespace MautVision.Desktop.Controllers.Jobs;

internal static class JobResponseMapper
{
    /// <summary>A job stores the code of whatever failed; the page translates it, the stored message stays for codes it does not know.</summary>
    internal static JobResponseDto Map(JobInfo job) => new(
        job.Id,
        job.Kind,
        job.ProjectId,
        job.OperationId,
        Map(job.Status),
        job.Progress,
        CodedTextDto.MapOrNull(job.ProgressMessage),
        job.Processed,
        job.Total,
        job.Error is { } error ? new JobErrorDto(error.Code, error.Message) : null,
        job.CancelRequested,
        job.CreatedAt,
        job.UpdatedAt,
        job.StartedAt,
        job.FinishedAt,
        job.Sequence,
        MapResult(job),
        CodedTextDto.MapOrNull(job.Label));

    private static readonly JsonSerializerOptions ResultJson = new(JsonSerializerDefaults.Web);

    /// <summary>Known results get their DTOs (codes, never texts); other kinds pass their JSON through unchanged.</summary>
    private static object? MapResult(JobInfo job)
    {
        if (job.Result is not { } result) return null;
        if (job.Kind == ImportKind && result.Deserialize<ImportClassAssetsResult>(ResultJson) is { } import)
        {
            return new ImportClassAssetsResultDto(import.Total, import.Added, import.Duplicates, import.Rejected,
                import.RejectedFiles.Select(file => new ImportRejectedFileDto(file.Name, file.Reason)).ToArray());
        }
        if (job.Kind == ImageImportKind && result.Deserialize<ImportImagesResult>(ResultJson) is { } images)
        {
            return new ImportImagesResultDto(images.Total, images.Added, images.Duplicates, images.Rejected,
                images.RejectedFiles.Select(file => new ImportRejectedFileDto(file.Name, file.Reason)).ToArray());
        }
        if (job.Kind == BackgroundImportKind && result.Deserialize<ImportBackgroundsResult>(ResultJson) is { } backgrounds)
        {
            return new ImportBackgroundsResultDto(backgrounds.Total, backgrounds.Processed, backgrounds.Added, backgrounds.Duplicates, backgrounds.Similar,
                backgrounds.Rejected, backgrounds.RequestedCount, BackgroundsResponseMapper.Map(backgrounds.TargetSize), BackgroundsResponseMapper.Map(backgrounds.Blur),
                backgrounds.Issues.Select(issue => new BackgroundImportIssueDto(issue.SourceIndex, issue.Name, issue.Reason,
                    issue.Width, issue.Height, issue.TimestampMs)).ToArray());
        }
        if (job.Kind == GenerationRunKind && result.Deserialize<GenerationRunResult>(ResultJson) is { } generation)
        {
            return new GenerationRunResultDto(generation.RequestedCount, generation.ProducedCount, generation.MissingObjectFrames, generation.OutputGroupId,
                generation.Seed);
        }
        if (job.Kind == DatasetBuildKind && result.Deserialize<DatasetBuildResult>(ResultJson) is { } build) return DatasetsResponseMapper.Map(build);
        if (job.Kind == DatasetVerifyKind && result.Deserialize<DatasetVerifyResult>(ResultJson) is { } verify) return DatasetsResponseMapper.Map(verify);
        if (job.Kind == DatasetExportKind && result.Deserialize<DatasetExportResult>(ResultJson) is { } export) return DatasetsResponseMapper.Map(export);
        if (job.Kind == TrainingRunKind && result.Deserialize<TrainingRunResult>(ResultJson) is { } training) return new TrainingRunResultDto(training.VersionId);
        if (job.Kind == ModelExportKind && result.Deserialize<ModelExportResult>(ResultJson) is { } model)
            return new ModelExportResultDto(model.VersionId, model.VersionNumber, model.FileName, model.Bytes, model.Sha256);
        return result;
    }

    private static readonly string DatasetBuildKind = KindOf(typeof(IDatasetBuildJob));

    private static readonly string DatasetVerifyKind = KindOf(typeof(IDatasetVerifyJob));

    private static readonly string DatasetExportKind = KindOf(typeof(IDatasetExportJob));

    private static readonly string TrainingRunKind = KindOf(typeof(ITrainingRunJob));

    private static readonly string ModelExportKind = KindOf(typeof(IModelExportJob));

    private static string KindOf(Type contract) => contract.GetCustomAttributes(typeof(JobKindAttribute), false).Cast<JobKindAttribute>().Single().Key;

    private static readonly string GenerationRunKind = typeof(IGenerationRunJob).GetCustomAttributes(typeof(JobKindAttribute), false)
        .Cast<JobKindAttribute>().Single().Key;

    private static readonly string ImageImportKind = typeof(IImportImagesJob).GetCustomAttributes(typeof(JobKindAttribute), false)
        .Cast<JobKindAttribute>().Single().Key;

    private static readonly string BackgroundImportKind = typeof(IImportBackgroundsJob).GetCustomAttributes(typeof(JobKindAttribute), false)
        .Cast<JobKindAttribute>().Single().Key;

    private static readonly string ImportKind = typeof(IImportClassAssetsJob).GetCustomAttributes(typeof(JobKindAttribute), false)
        .Cast<JobKindAttribute>().Single().Key;

    /// <summary>The status as the UI and MCP clients receive it: queued, running, succeeded, failed, cancelled, interrupted, blocked.</summary>
    internal static string MapStatus(JobStatus status) => Map(status);

    private static string Map(JobStatus status) => status switch
    {
        JobStatus.Queued => "queued",
        JobStatus.Running => "running",
        JobStatus.Succeeded => "succeeded",
        JobStatus.Failed => "failed",
        JobStatus.Cancelled => "cancelled",
        JobStatus.Interrupted => "interrupted",
        JobStatus.Blocked => "blocked",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown job status."),
    };
}
