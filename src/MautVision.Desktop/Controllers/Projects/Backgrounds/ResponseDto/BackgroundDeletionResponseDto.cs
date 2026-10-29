using MautVision.Desktop.Controllers.Projects.Catalog.ResponseDto;

namespace MautVision.Desktop.Controllers.Projects.Backgrounds.ResponseDto;

/// <summary>
/// What a permanent deletion removes: the whole set (WholeSet) or the selected backgrounds. Items — "Backgrounds to delete: N."; non-empty Blockers —
/// "Finish the jobs or change the dependencies before deleting:" + Message lines. Revision is passed to the deletion.
/// </summary>
public sealed record BackgroundDeletionReviewResponseDto(
    Guid SetId,
    string SetName,
    bool WholeSet,
    long Items,
    DeletionBlockerResponseDto[] Blockers,
    string Revision);

/// <summary>Rows are deleted; CleanupPending — files are still being deleted in the background, the cleanup continues the next time the project is opened.</summary>
public sealed record BackgroundDeletionResultResponseDto(Guid SetId, bool SetDeleted, long Items, bool CleanupPending);

/// <summary>Changed or deleted (Revision = null) set or background.</summary>
public sealed record BackgroundsEntityChangeDto(Guid Id, long? Revision);

/// <summary>
/// The backgrounds.changed event. Sets — sets with new revisions or deleted ones; Items — backgrounds changed by an explicit operation;
/// ItemsChangedIn — sets whose content or counters changed: the UI rereads their set and pages.
/// </summary>
public sealed record BackgroundsChangedEventDto(string ProjectId, BackgroundsEntityChangeDto[] Sets, BackgroundsEntityChangeDto[] Items, Guid[] ItemsChangedIn);
