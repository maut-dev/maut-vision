namespace MautVision.Core.Interfaces.Jobs;

/// <summary>Active job: WaitingForGpu — the job is queued for the GPU (training, model check).</summary>
public sealed record ActiveJobEntry(JobInfo Job, bool WaitingForGpu);

/// <summary>
/// Page of active jobs in queue order. Total — all active jobs of the project, CountsByKind — their number by kind;
/// NextCursor — ID of the last job of the page if more follow, otherwise null.
/// </summary>
public sealed record ActiveJobsPage(IReadOnlyList<ActiveJobEntry> Items, int Total, IReadOnlyDictionary<string, int> CountsByKind, Guid? NextCursor);
