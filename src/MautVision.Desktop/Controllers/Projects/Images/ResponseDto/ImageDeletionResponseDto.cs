using MautVision.Desktop.Controllers.Projects.Catalog.ResponseDto;

namespace MautVision.Desktop.Controllers.Projects.Images.ResponseDto;

/// <summary>
/// What a permanent deletion removes: the whole group (WholeGroup) or the selected images. Revision is passed to the deletion;
/// non-empty Blockers — the deletion is unavailable now (list lines — Message).
/// </summary>
public sealed record ImageDeletionReviewResponseDto(
    Guid GroupId,
    string GroupName,
    bool WholeGroup,
    long Images,
    DeletionBlockerResponseDto[] Blockers,
    string Revision);

/// <summary>Rows are deleted; CleanupPending — files are still being deleted in the background, the cleanup continues the next time the project is opened.</summary>
public sealed record ImageDeletionResultResponseDto(Guid GroupId, bool GroupDeleted, long Images, bool CleanupPending);

/// <summary>Changed or deleted (Revision = null) group or image.</summary>
public sealed record ImagesEntityChangeDto(Guid Id, long? Revision);

/// <summary>
/// The images.changed event. Groups — groups with new revisions or deleted ones; Images — images changed by an explicit
/// operation; ImagesChangedIn — groups whose content or counters changed: the UI rereads their group and pages.
/// </summary>
public sealed record ImagesChangedEventDto(
    string ProjectId,
    ImagesEntityChangeDto[] Groups,
    ImagesEntityChangeDto[] Images,
    Guid[] ImagesChangedIn);
