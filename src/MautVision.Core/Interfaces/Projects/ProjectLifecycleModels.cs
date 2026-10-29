namespace MautVision.Core.Interfaces.Projects;

/// <summary>
/// What is running in the project now: an active job (Kind — its kind, JobId, Label — caption) or a live model check
/// (Kind <see cref="LiveCheckKind"/>, JobId null).
/// </summary>
public sealed record ProjectActivity(string Kind, Guid? JobId, CodedText? Label)
{
    public const string LiveCheckKind = "checks.live";
}

/// <summary>
/// Deletion confirmation: Revision — the revision the user sees (passed to DeleteAsync); Activities — running jobs and
/// the live check that the deletion cancels; TransferActive — a project export is running, the deletion would be refused now.
/// </summary>
public sealed record ProjectDeletionReview(string ProjectId, string Name, long Revision, IReadOnlyList<ProjectActivity> Activities, bool TransferActive);

/// <summary>CleanupPending — the project is deleted but some of its files are locked; the cleanup is repeated on the next start.</summary>
public sealed record ProjectDeletionResult(string ProjectId, bool CleanupPending);

public sealed class ProjectRemovedEventArgs(string projectId) : EventArgs
{
    public string ProjectId { get; } = projectId;
}
