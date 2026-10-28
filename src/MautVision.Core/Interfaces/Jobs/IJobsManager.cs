namespace MautVision.Core.Interfaces.Jobs;

/// <summary>
/// Jobs of a project are kept in its project.db and run only while the project is open
/// (<see cref="Projects.IProjectsManager.OpenAsync"/>). Operations on jobs of a project that was not opened in this
/// process are refused with the code <c>jobs.project_not_open</c>. There are no global jobs.
/// </summary>
public interface IJobsManager
{
    /// <summary>A snapshot after every saved change, in Sequence order; may come from a background thread.</summary>
    event EventHandler<JobChangedEventArgs>? JobChanged;

    /// <summary>
    /// Saves the job and returns its snapshot at once, without waiting for it to run. A repeat with the same
    /// operationId in the project returns the existing job. An error of the run is saved in the job.
    /// label — what lists of jobs show after the kind: a name as a text (a dataset) or a code with values (a version number); the text
    /// is trimmed and cut to <see cref="JobInfo.MaxLabelLength"/> characters, an empty text is null.
    /// </summary>
    Task<JobInfo> EnqueueAsync<TJob, TInput>(TInput input, string projectId, string operationId, CodedText? label = null,
        CancellationToken cancellationToken = default) where TJob : IJobDefinition<TInput>;

    /// <summary>A job by ID; a missing one is CoreException <c>jobs.not_found</c>.</summary>
    Task<JobInfo> GetAsync(string projectId, Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>Jobs of the project, newest first, at most limit (1–200); before — the ID of the last job of the previous page.</summary>
    Task<IReadOnlyList<JobInfo>> ListAsync(string projectId, int limit = 50, Guid? before = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Active jobs of the project (queued and running, cancelling ones included) in queue order, at most limit (1–200); after —
    /// NextCursor of the previous page. Total and CountsByKind are exact numbers of all active jobs, not only of the page.
    /// </summary>
    Task<ActiveJobsPage> ListActiveAsync(string projectId, int limit = 30, Guid? after = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Idempotent cancellation. A queued or Blocked job is cancelled at once; a running one gets CancelRequested
    /// and becomes Cancelled when its handler stops. A finished job does not change.
    /// </summary>
    Task<JobInfo> CancelAsync(string projectId, Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>Active jobs (queued and running) of all projects open in this process, from memory; for example, before exit.</summary>
    int CountActive();
}
