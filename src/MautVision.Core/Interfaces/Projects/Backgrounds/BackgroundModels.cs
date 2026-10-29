using MautVision.Core.Interfaces.Projects.Catalog;

namespace MautVision.Core.Interfaces.Projects.Backgrounds;

/// <summary>
/// Background set. TargetSize — a copy of the project input size at creation, never changes; MatchesProjectInput — false if the
/// project input size changed later ("Set size W × H differs from the project input"). Revision grows on a change of name,
/// description and archive; ContentRevision — on every content change (adding, "Remove", "Restore", deletion).
/// ItemCount — active backgrounds, RemovedCount — removed ones.
/// </summary>
public sealed record BackgroundSet(
    Guid Id,
    string Name,
    string Description,
    InputSize TargetSize,
    long Revision,
    long ContentRevision,
    long ItemCount,
    long RemovedCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    bool MatchesProjectInput);

/// <summary>Background origin: a ready image, an image fragment or a video frame.</summary>
public enum BackgroundOriginKind
{
    Uploaded,
    ImageCrop,
    VideoFrame,
}

/// <summary>Cut-out area of the source frame in its pixels (with EXIF orientation applied); the size is the set size.</summary>
public sealed record BackgroundCropArea(int Left, int Top, int Width, int Height);

/// <summary>
/// Where the background comes from. SourceName — file or video name; SourceHash — SHA-256 of the source file (the video for a frame); video frame
/// fields are filled only for VideoFrame. Crop — the cut-out area (null — the whole frame is used). BlurRadius — blur radius
/// (0 — the background is left sharp), null — a run without blur. ProcessorVersion — version of the frame processing.
/// </summary>
public sealed record BackgroundOrigin(
    BackgroundOriginKind Kind,
    string OperationId,
    string SourceName,
    string SourceHash,
    string? VideoName,
    long? TimestampMs,
    long? FrameIndex,
    BackgroundCropArea? Crop,
    double? BlurRadius,
    string ProcessorVersion);

/// <summary>
/// Background — an RGBA PNG of exactly the set size, the single file files/backgrounds/&lt;id&gt;.png. PixelHash — SHA-256 of the pixels
/// (exact duplicates in the set). Revision grows on "Remove" and "Restore"; RemovedAt — removed (reversible).
/// The order in the set is the order of adding.
/// </summary>
public sealed record BackgroundItem(
    Guid Id,
    Guid SetId,
    int Width,
    int Height,
    long ByteSize,
    string Sha256,
    string PixelHash,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RemovedAt,
    BackgroundOrigin Origin)
{
    /// <summary>Material kind in the resource channel: <c>new ResourceReference(ResourceKind, item.Id)</c>.</summary>
    public const string ResourceKind = "backgrounds";
}

/// <summary>Removed: null — all, true — only removed, false — only active.</summary>
public sealed record BackgroundFilter(bool? Removed = false);

/// <summary>Page of backgrounds of a set in the order of adding; NextCursor — null on the last page.</summary>
public sealed record BackgroundPage(IReadOnlyList<BackgroundItem> Items, string? NextCursor);

/// <summary>
/// Page of active backgrounds of a set for generation: ContentRevision and TargetSize are read together with the page. If the content
/// revision differs between pages, the set content changed during reading.
/// </summary>
public sealed record BackgroundActivePage(Guid SetId, InputSize TargetSize, long ContentRevision, IReadOnlyList<BackgroundItem> Items, string? NextCursor);

/// <summary>Background with the revision the user saw.</summary>
public sealed record BackgroundRevision(Guid Id, long Revision);

/// <summary>
/// What a permanent deletion removes: the whole set (WholeSet) or the selected backgrounds. Revision is passed to the deletion; non-empty Blockers —
/// the deletion is unavailable now (<c>backgrounds.import_active</c>, generation presets).
/// </summary>
public sealed record BackgroundDeletionReview(Guid SetId, string SetName, bool WholeSet, long Items, IReadOnlyList<DeletionBlocker> Blockers, string Revision);

/// <summary>Rows are deleted; CleanupPending — files are still being deleted in the background.</summary>
public sealed record BackgroundDeletionResult(Guid SetId, bool SetDeleted, long Items, bool CleanupPending);

/// <summary>How a file fits the set: exactly the right size, larger (can be cut with a frame), smaller or unreadable.</summary>
public enum BackgroundSourceFit
{
    Exact,
    Larger,
    Smaller,
    Unreadable,
}

/// <summary>Header of a chosen file: name without path, size with EXIF orientation (null if unreadable) and fit.</summary>
public sealed record BackgroundSourceInfo(string Name, int? Width, int? Height, BackgroundSourceFit Fit);

/// <summary>Encoded image for display (PNG or JPEG); not stored in the project.</summary>
public sealed record BackgroundPreviewImage(byte[] Content, string ContentType, int Width, int Height);

/// <summary>Frame for the crop frame: source frame size (with orientation) and a reduced copy no larger than the given side.</summary>
public sealed record BackgroundSourcePreview(int Width, int Height, BackgroundPreviewImage Preview);

/// <summary>
/// Sample of a future background. SourceIndex — index of the run source (for video — index of the frame in the range), TimestampMs — frame mark.
/// Reason — skip reason code (then Image is null): <c>backgrounds.wrong_size</c>, <c>backgrounds.crop_out_of_bounds</c>
/// (with ActualWidth/ActualHeight), <c>backgrounds.image_unreadable</c>, <c>backgrounds.similar</c>, <c>backgrounds.duplicate</c>,
/// <c>video.frame_unavailable</c>. Image — a PNG of exactly the set size, the same pixels a run with this operation produces.
/// </summary>
public sealed record BackgroundImportSample(
    int SourceIndex,
    string SourceName,
    long? TimestampMs,
    string? Reason,
    int? ActualWidth,
    int? ActualHeight,
    BackgroundCropArea? Crop,
    double? BlurRadius,
    BackgroundPreviewImage? Image);

/// <summary>Run samples: set size, blur with the chosen seed (null — no blur) and up to 6 samples.</summary>
public sealed record BackgroundImportPreview(InputSize TargetSize, BackgroundBlur? Blur, IReadOnlyList<BackgroundImportSample> Samples);
