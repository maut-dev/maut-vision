using MautVision.Desktop.Controllers.Jobs.ResponseDto;

namespace MautVision.Desktop.Controllers.Projects.Overview.ResponseDto;

/// <summary>Ready — not archived and not corrupt; Archived — archived; Corrupt — corrupt and not archived.</summary>
public sealed record OverviewDatasetsDto(int Ready, int Archived, int Corrupt);

/// <summary>A row of "Latest models": RunId leads to its training run, Metrics are the keys of the version (mAP50, mAP50-95…).</summary>
public sealed record OverviewModelVersionDto(Guid Id, int Number, bool IsWorking, DateTimeOffset CreatedAt, string Architecture,
    IReadOnlyDictionary<string, double> Metrics, Guid RunId);

/// <summary>
/// overview.get: the counters of "Project data", the 5 latest model versions (newest first) and the first page of active jobs with
/// their exact total and counts by kind. The page refreshes it on job.changed, datasets.changed, training.changed and checks.changed.
/// </summary>
public sealed record OverviewResponseDto(string ProjectId, DateTimeOffset CapturedAt, OverviewDatasetsDto Datasets, int ModelVersionCount,
    OverviewModelVersionDto[] LatestVersions, ActiveJobsPageResponseDto ActiveJobs);
