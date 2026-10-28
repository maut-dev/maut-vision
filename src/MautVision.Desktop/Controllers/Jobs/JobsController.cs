using MautVision.Core.Interfaces.Jobs;
using MautVision.Desktop.Controllers.Jobs.RequestDto;
using MautVision.Desktop.Controllers.Jobs.ResponseDto;

namespace MautVision.Desktop.Controllers.Jobs;

/// <summary>Reads and cancels jobs. Jobs are started by the methods of their features, not by a generic enqueue.</summary>
public sealed class JobsController(IJobsManager jobs, Func<string, string> messageFor)
{
    public async Task<JobResponseDto[]> ListAsync(ListJobsRequestDto request, CancellationToken cancellationToken)
        => (await jobs.ListAsync(request.ProjectId, request.Limit ?? 50, request.Before, cancellationToken))
            .Select(job => JobResponseMapper.Map(job, messageFor)).ToArray();

    public async Task<ActiveJobsPageResponseDto> ListActiveAsync(ListActiveJobsRequestDto request, CancellationToken cancellationToken)
        => Map(await jobs.ListActiveAsync(request.ProjectId, request.Limit ?? 30, request.After, cancellationToken), messageFor);

    internal static ActiveJobsPageResponseDto Map(ActiveJobsPage page, Func<string, string> messageFor) => new(
        page.Items.Select(item => new ActiveJobResponseDto(JobResponseMapper.Map(item.Job, messageFor), item.WaitingForGpu)).ToArray(),
        page.Total,
        page.CountsByKind,
        page.NextCursor);

    public async Task<JobResponseDto> GetAsync(GetJobRequestDto request, CancellationToken cancellationToken)
        => JobResponseMapper.Map(await jobs.GetAsync(request.ProjectId, request.JobId, cancellationToken), messageFor);

    public async Task<JobResponseDto> CancelAsync(CancelJobRequestDto request, CancellationToken cancellationToken)
        => JobResponseMapper.Map(await jobs.CancelAsync(request.ProjectId, request.JobId, cancellationToken), messageFor);
}
