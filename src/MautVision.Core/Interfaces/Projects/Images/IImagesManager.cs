using MautVision.Core.Interfaces.Jobs;

namespace MautVision.Core.Interfaces.Projects.Images;

/// <summary>
/// Project image groups, images and their annotation. Work only for an open project, otherwise CoreException
/// <c>images.project_not_open</c>. Changes check the revision (<c>images.revision_conflict</c>) and are reported by the event
/// <see cref="IProjectsManager.ImagesChanged"/>. Images are added by the job <see cref="IImportImagesJob"/>.
/// Missing group or image — <c>images.group_not_found</c>, <c>images.image_not_found</c>.
/// An archived group is read-only (<c>images.group_archived</c>), except for restoring and deletion; a group that
/// generation is filling — <c>images.group_busy</c>.
/// </summary>
public interface IImagesManager
{
    /// <summary>All project groups, including archived ones, by creation time.</summary>
    Task<IReadOnlyList<ImageGroup>> ListGroupsAsync(CancellationToken cancellationToken = default);

    Task<ImageGroup> GetGroupAsync(Guid groupId, CancellationToken cancellationToken = default);

    /// <summary>Manual group. Errors: <c>images.name_invalid</c>, <c>images.description_invalid</c>.</summary>
    Task<ImageGroup> CreateGroupAsync(string name, string description, ImageSplitHint splitHint, CancellationToken cancellationToken = default);

    Task<ImageGroup> UpdateGroupAsync(Guid groupId, string name, string description, ImageSplitHint splitHint, long expectedRevision,
        CancellationToken cancellationToken = default);

    /// <summary>A group being imported into cannot be archived (<c>images.group_importing</c>).</summary>
    Task<ImageGroup> SetGroupArchivedAsync(Guid groupId, bool archived, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>Page in import order; limit 1–200, after — NextCursor of the previous page of the same filter.</summary>
    Task<ImagePage> ListImagesAsync(Guid groupId, ImageFilter filter, int limit = 100, string? after = null, CancellationToken cancellationToken = default);

    Task<ProjectImage> GetImageAsync(Guid imageId, CancellationToken cancellationToken = default);

    /// <summary>The current annotation revision or the given one (history is immutable).</summary>
    Task<ImageAnnotation> GetAnnotationAsync(Guid imageId, long? revision = null, CancellationToken cancellationToken = default);

    /// <summary>Current annotation of up to 200 images in one request (boxes on page thumbnails); missing ones are skipped.</summary>
    Task<IReadOnlyList<ImageAnnotation>> ListAnnotationsAsync(IReadOnlyList<Guid> imageIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a new annotation revision. expectedAnnotationRevision — the AnnotationRevision the user saw.
    /// Boxes 0–1 (<c>images.box_invalid</c>); object Id — from the current revision (<c>images.object_id_invalid</c>);
    /// new objects and objects changed by class or variant refer to an active class and class variant
    /// (<c>images.reference_unavailable</c>), unchanged ones may keep an archived reference. An archived image —
    /// <c>images.image_archived</c>.
    /// </summary>
    Task<SavedAnnotation> SaveAnnotationAsync(Guid imageId, AnnotationInput annotation, long expectedAnnotationRevision,
        CancellationToken cancellationToken = default);

    /// <summary>Archiving and restoring the selected images (up to 400) — all or nothing, with the revision of each.</summary>
    Task<IReadOnlyList<ProjectImage>> SetImagesArchivedAsync(IReadOnlyList<ImageRevision> images, bool archived,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves active images (up to 400) to another group of the same origin (<c>images.origin_mismatch</c>) —
    /// all or nothing, with the revisions of both groups and of each image. The images go to the end of the target order.
    /// </summary>
    Task<MovedImages> MoveImagesAsync(Guid sourceGroupId, long sourceRevision, Guid targetGroupId, long targetRevision,
        IReadOnlyList<ImageRevision> images, CancellationToken cancellationToken = default);

    /// <summary>
    /// Import history of a group: <see cref="IImportImagesJob"/> jobs of this group, newest first (limit 1–200, before — ID
    /// of the last job of the previous page). The result with per-file errors is in <see cref="JobInfo.Result"/>.
    /// </summary>
    Task<IReadOnlyList<JobInfo>> ListImportsAsync(Guid groupId, int limit = 50, Guid? before = null, CancellationToken cancellationToken = default);

    /// <summary>What deleting the whole group removes.</summary>
    Task<ImageDeletionReview> ReviewGroupDeletionAsync(Guid groupId, CancellationToken cancellationToken = default);

    /// <summary>Deletes the group, its images, annotation and import history. Errors: <c>images.deletion_revision_conflict</c>, <c>images.deletion_blocked</c>.</summary>
    Task<ImageDeletionResult> DeleteGroupAsync(Guid groupId, string reviewRevision, CancellationToken cancellationToken = default);

    /// <summary>What deleting the selected images (up to 400) of one group removes.</summary>
    Task<ImageDeletionReview> ReviewImagesDeletionAsync(Guid groupId, IReadOnlyList<Guid> imageIds, CancellationToken cancellationToken = default);

    Task<ImageDeletionResult> DeleteImagesAsync(Guid groupId, IReadOnlyList<Guid> imageIds, string reviewRevision,
        CancellationToken cancellationToken = default);
}
