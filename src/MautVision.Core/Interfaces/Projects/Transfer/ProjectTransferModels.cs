using MautVision.Core.Interfaces.Jobs;

namespace MautVision.Core.Interfaces.Projects.Transfer;

/// <summary>DestinationPath — the .zip chosen by the user; ProjectName — the job caption.</summary>
public sealed record ProjectExportInput(string ProjectName, string DestinationPath);

/// <summary>
/// Project export into one ZIP (project.json, a snapshot of project.db, files/**): a CPU job, not resumable, with progress by files
/// (Processed/Total) and bytes (result). While it is queued or running, other jobs and live checks of the project do not start
/// (<c>projects.transfer_active</c>); it is enqueued only when the project has no other active jobs and no live check
/// (<c>projects.transfer_busy</c>, Details["section"] — the area of the first running job). The archive is written into a temporary file next to
/// the target and replaces it at the end; cancellation and errors delete the temporary file. The result is <see cref="ProjectExportResult"/>.
/// </summary>
[JobKind("projects.export")]
public interface IProjectExportJob : IJobDefinition<ProjectExportInput>
{
}

/// <summary>
/// Export result (also after cancellation or an error — how much was done): Files — written files, Links — hard link paths written to
/// the manifest without repeating the content, Bytes — content volume, ArchiveBytes — archive size.
/// </summary>
public sealed record ProjectExportResult(string FileName, long Files, long TotalFiles, long Bytes, long TotalBytes, long Links, long ArchiveBytes,
    long ElapsedMs);

/// <summary>Archive chosen by the user: path, size and modification time at the time of choosing (a file changed after choosing is not read).</summary>
public sealed record ProjectArchiveSource(string Path, long Length, DateTimeOffset LastWriteTimeUtc);

public enum ProjectImportStatus
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// Import snapshot. Sequence grows with every change: of two snapshots, the one with the greater value is newer. Processed/Total counters —
/// unpacked files and bytes (Total is known after the archive check, 0 before it). On success ProjectId and ProjectName — the project in
/// the library (a new id on a collision; a taken name gets the suffix " (&lt;label&gt;)" with the word the UI passes, English "import" by default); ErrorCode — refusal code on Failed.
/// The import lives in process memory and does not survive a restart.
/// </summary>
public sealed record ProjectImportInfo(
    Guid Id,
    string OperationId,
    string FileName,
    ProjectImportStatus Status,
    bool CancelRequested,
    long ProcessedFiles,
    long TotalFiles,
    long ProcessedBytes,
    long TotalBytes,
    string? ProjectId,
    string? ProjectName,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    long Sequence);

public sealed class ProjectImportChangedEventArgs(ProjectImportInfo import) : EventArgs
{
    public ProjectImportInfo Import { get; } = import;
}
