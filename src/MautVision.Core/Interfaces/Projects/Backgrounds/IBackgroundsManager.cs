using MautVision.Core.Interfaces.Jobs;

namespace MautVision.Core.Interfaces.Projects.Backgrounds;

/// <summary>
/// Background sets of the project and their backgrounds. Work only for an open project, otherwise CoreException <c>backgrounds.project_not_open</c>.
/// Changes check the revision (<c>backgrounds.revision_conflict</c>) and are reported by the event
/// <see cref="IProjectsManager.BackgroundsChanged"/>. Backgrounds are added by the job <see cref="IImportBackgroundsJob"/>.
/// Missing set or background — <c>backgrounds.set_not_found</c>, <c>backgrounds.item_not_found</c>. An archived set is
/// read-only (<c>backgrounds.set_archived</c>), except for restoring and deletion.
/// </summary>
public interface IBackgroundsManager
{
    /// <summary>All sets, including archived ones, by creation time.</summary>
    Task<IReadOnlyList<BackgroundSet>> ListSetsAsync(CancellationToken cancellationToken = default);

    Task<BackgroundSet> GetSetAsync(Guid setId, CancellationToken cancellationToken = default);

    /// <summary>
    /// A set of the current project input size. Name 1–120 characters (<c>backgrounds.name_invalid</c>), description up to 4000
    /// (<c>backgrounds.description_invalid</c>).
    /// </summary>
    Task<BackgroundSet> CreateSetAsync(string name, string description, CancellationToken cancellationToken = default);

    Task<BackgroundSet> UpdateSetAsync(Guid setId, string name, string description, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>A set being prepared cannot be archived (<c>backgrounds.set_importing</c>).</summary>
    Task<BackgroundSet> SetSetArchivedAsync(Guid setId, bool archived, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>Page in the order of adding; limit 1–200, after — NextCursor of the previous page of the same filter.</summary>
    Task<BackgroundPage> ListItemsAsync(Guid setId, BackgroundFilter filter, int limit = 100, string? after = null, CancellationToken cancellationToken = default);

    Task<BackgroundItem> GetItemAsync(Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// "Remove" (removed: true) or "Restore" the selected backgrounds (up to 400) of one set — all or nothing, with the revision of each
    /// background; the set revision is not needed, so a running import causes no conflicts.
    /// </summary>
    Task<IReadOnlyList<BackgroundItem>> SetItemsRemovedAsync(IReadOnlyList<BackgroundRevision> items, bool removed, CancellationToken cancellationToken = default);

    /// <summary>
    /// Active backgrounds of a set for generation in the order of adding (limit 1–1000) together with the content revision and set size.
    /// </summary>
    Task<BackgroundActivePage> ListActiveItemsAsync(Guid setId, int limit = 500, string? after = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Preparation history of a set: <see cref="IImportBackgroundsJob"/> jobs of this set, newest first (limit 1–200, before —
    /// ID of the last job of the previous page). The result is <see cref="ImportBackgroundsResult"/> in <see cref="JobInfo.Result"/>.
    /// </summary>
    Task<IReadOnlyList<JobInfo>> ListRunsAsync(Guid setId, int limit = 50, Guid? before = null, CancellationToken cancellationToken = default);

    Task<BackgroundDeletionReview> ReviewSetDeletionAsync(Guid setId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the set, its backgrounds and preparation history; files in the background. Errors: <c>backgrounds.deletion_revision_conflict</c>,
    /// <c>backgrounds.deletion_blocked</c>.
    /// </summary>
    Task<BackgroundDeletionResult> DeleteSetAsync(Guid setId, string reviewRevision, CancellationToken cancellationToken = default);

    /// <summary>What deleting the selected backgrounds (up to 400) of one set removes; <c>backgrounds.selection_invalid</c> for an empty selection.</summary>
    Task<BackgroundDeletionReview> ReviewItemsDeletionAsync(Guid setId, IReadOnlyList<Guid> itemIds, CancellationToken cancellationToken = default);

    Task<BackgroundDeletionResult> DeleteItemsAsync(Guid setId, IReadOnlyList<Guid> itemIds, string reviewRevision, CancellationToken cancellationToken = default);

    /// <summary>
    /// Headers of the chosen files (without full decoding): size with orientation and fit for the set — before the run
    /// it is visible how many files have the right size and how many will be skipped. Paths are absolute; the answer follows the path order.
    /// </summary>
    Task<IReadOnlyList<BackgroundSourceInfo>> InspectFilesAsync(Guid setId, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>
    /// File frame for the crop frame: scaled decoding to maxSide (64–4096) along the longer side. An unreadable file —
    /// <c>backgrounds.image_unreadable</c>.
    /// </summary>
    Task<BackgroundSourcePreview> PreviewFileAsync(string path, int maxSide = 1600, CancellationToken cancellationToken = default);

    /// <summary>
    /// Video frame at a mark (the first frame not earlier than it) for the crop frame, reduced to maxSide. A mark past the end of the video track —
    /// <c>video.frame_unavailable</c>; read errors — <c>video.*</c> codes.
    /// </summary>
    Task<BackgroundSourcePreview> PreviewVideoFrameAsync(string path, long timestampMs, int maxSide = 1600, CancellationToken cancellationToken = default);

    /// <summary>
    /// Up to 6 run samples with the same input and operationId: the same crops, radii and pixels the job produces. Samples
    /// are spread across sources; for video — over the first RequestedCount frames, moving on to further frames, and with SkipSimilar a similar
    /// frame is replaced by the next one. Input errors are the same as for the job.
    /// </summary>
    Task<BackgroundImportPreview> PreviewImportAsync(ImportBackgroundsInput input, string operationId, CancellationToken cancellationToken = default);
}
