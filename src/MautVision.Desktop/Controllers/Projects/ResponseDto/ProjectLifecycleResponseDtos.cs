using MautVision.Desktop.Controllers.Jobs.ResponseDto;

namespace MautVision.Desktop.Controllers.Projects.ResponseDto;

/// <summary>Library: all projects and the last opened one (null if there is none or it is deleted).</summary>
public sealed record ProjectLibraryResponseDto(ProjectResponseDto[] Items, string? LastProjectId);

/// <summary>Export save location; null — the window was closed without a choice.</summary>
public sealed record ProjectExportSelectionResponseDto(string? SelectionToken, string? FileName);

/// <summary>The chosen archive; null — the window was closed without a choice.</summary>
public sealed record ProjectArchiveSelectionResponseDto(string? SelectionToken, string? FileName, long? ByteSize);

/// <summary>
/// Import: Status — running | succeeded | failed | cancelled; counters of unpacked files and bytes (Total — 0 until the archive is checked);
/// ProjectId and ProjectName — the added project; Error — refusal code and text. Sequence grows with every snapshot.
/// </summary>
public sealed record ProjectImportResponseDto(
    Guid Id,
    string OperationId,
    string FileName,
    string Status,
    bool CancelRequested,
    long ProcessedFiles,
    long TotalFiles,
    long ProcessedBytes,
    long TotalBytes,
    string? ProjectId,
    string? ProjectName,
    JobErrorDto? Error,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    long Sequence);

/// <summary>Running work of the project: a job (Kind — its kind, JobId, Label) or a live check (Kind checks.live).</summary>
public sealed record ProjectActivityResponseDto(string Kind, Guid? JobId, CodedTextDto? Label);

/// <summary>Activities — what the deletion cancels; TransferActive — an export is running, the deletion would be refused.</summary>
public sealed record ProjectDeletionReviewResponseDto(string ProjectId, string Name, long Revision, ProjectActivityResponseDto[] Activities, bool TransferActive);

/// <summary>CleanupPending — some files are locked, the cleanup is repeated on the next start.</summary>
public sealed record ProjectDeletionResponseDto(string ProjectId, bool CleanupPending);

/// <summary>The project.removed event.</summary>
public sealed record ProjectRemovedEventDto(string ProjectId);
