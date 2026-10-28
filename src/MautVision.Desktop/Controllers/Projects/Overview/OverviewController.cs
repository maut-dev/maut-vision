using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects;
using MautVision.Desktop.Controllers.Jobs;
using MautVision.Desktop.Controllers.Projects.Overview.RequestDto;
using MautVision.Desktop.Controllers.Projects.Overview.ResponseDto;

namespace MautVision.Desktop.Controllers.Projects.Overview;

/// <summary>The overview of an opened project, composed from its managers and the jobs of Core; it holds no logic of its own.</summary>
internal sealed class OverviewController(IProjectsManager projects, IJobsManager jobs, Func<string, string> messageFor)
{
    internal const int LatestVersions = 5;
    internal const int ActiveJobsPage = 30;

    public async Task<OverviewResponseDto> GetAsync(GetOverviewRequestDto request, CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(request.ProjectId, cancellationToken);
        var capturedAt = DateTimeOffset.UtcNow;
        var datasets = await project.Datasets.GetSummaryAsync(cancellationToken);
        var versions = await project.Training.ListVersionsAsync(cancellationToken);
        var active = await jobs.ListActiveAsync(project.Id, ActiveJobsPage, null, cancellationToken);
        return new OverviewResponseDto(
            project.Id,
            capturedAt,
            new OverviewDatasetsDto(datasets.Ready, datasets.Archived, datasets.Corrupt),
            versions.Count,
            versions.OrderByDescending(version => version.Number).Take(LatestVersions)
                .Select(version => new OverviewModelVersionDto(version.Id, version.Number, version.IsWorking, version.CreatedAt, version.Architecture,
                    version.Metrics, version.RunId)).ToArray(),
            JobsController.Map(active, messageFor));
    }
}
