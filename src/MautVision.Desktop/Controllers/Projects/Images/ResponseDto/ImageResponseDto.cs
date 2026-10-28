namespace MautVision.Desktop.Controllers.Projects.Images.ResponseDto;

/// <summary>
/// Frame origin. Kind: file, video, generated. SourceFamilyId joins frames of one source. Video fields
/// (VideoName, TimestampMs, FrameIndex) and generation fields (GenerationRunId, SampleIndex, RecipeRevision) are null for other kinds.
/// </summary>
public sealed record ImageLineageResponseDto(
    string Kind,
    string SourceFamilyId,
    string? VideoName,
    long? TimestampMs,
    long? FrameIndex,
    Guid? GenerationRunId,
    int? SampleIndex,
    long? RecipeRevision);

/// <summary>
/// Group image. Format: png, jpeg, webp, bmp. Width/Height — with EXIF orientation applied (as &lt;img&gt; shows it).
/// Revision grows on saving the annotation, archiving and moving; AnnotationRevision — the current annotation revision (it is
/// passed to images.saveAnnotation). ObjectCount and ReviewState (unreviewed, reviewed) — from the current annotation.
/// OriginalUrl and ThumbnailUrl (JPEG up to 256 px) — resource channel URLs valid for a limited time.
/// </summary>
public sealed record ImageResponseDto(
    Guid Id,
    Guid GroupId,
    string DisplayName,
    string Format,
    int Width,
    int Height,
    long ByteSize,
    long Revision,
    long AnnotationRevision,
    int ObjectCount,
    string ReviewState,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ArchivedAt,
    ImageLineageResponseDto Lineage,
    string OriginalUrl,
    string ThumbnailUrl);

/// <summary>Page in import order; NextCursor — the after value for the next page of the same filter, null on the last one.</summary>
public sealed record ImagePageResponseDto(ImageResponseDto[] Items, string? NextCursor);

/// <summary>Move result: both groups with new revisions and the moved images.</summary>
public sealed record MovedImagesResponseDto(ImageGroupResponseDto Source, ImageGroupResponseDto Target, ImageResponseDto[] Images);

/// <summary>
/// Selection in a native dialog. Kind: files or folder. SelectionToken — null if the user closed the dialog; valid for
/// 30 minutes and only on this page. FileCount — number of selected files (null for a folder: the job determines the content).
/// FolderName — name of the selected folder without path (null for files). Paths are not passed to the UI.
/// </summary>
public sealed record ImageSelectionResponseDto(string Kind, string? SelectionToken, int? FileCount, string? FolderName);
