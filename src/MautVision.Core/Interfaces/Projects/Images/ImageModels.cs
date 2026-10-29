using MautVision.Core.Interfaces.Projects.Catalog;

namespace MautVision.Core.Interfaces.Projects.Images;

/// <summary>The group origin is set at creation: manual groups are filled by import, generated ones by generation.</summary>
public enum ImageGroupOrigin
{
    Manual,
    Generated,
}

/// <summary>Group assignment when a dataset is built: "Unassigned / Training / Validation / Test".</summary>
public enum ImageSplitHint
{
    Unassigned,
    Train,
    Validation,
    Test,
}

/// <summary>Fill state of a generated group; null for manual groups.</summary>
public enum ImageGenerationState
{
    Producing,
    Completed,
    Partial,
}

/// <summary>Reviewed — the annotation is reviewed, including a deliberately empty frame.</summary>
public enum ImageReviewState
{
    Unreviewed,
    Reviewed,
}

/// <summary>Format of the source file; detected by content.</summary>
public enum ImageFileFormat
{
    Png,
    Jpeg,
    Webp,
    Bmp,
}

public enum ImageLineageKind
{
    File,
    Video,
    Generated,
}

/// <summary>
/// Image group. Name 1–120 characters (duplicates allowed), description up to 4000. ImageCount — all images of the group,
/// including archived ones; ArchivedImageCount — archived ones. Revision grows on a change of group fields, archiving and moving
/// images; import does not change it (only the counters change).
/// </summary>
public sealed record ImageGroup(
    Guid Id,
    string Name,
    string Description,
    ImageGroupOrigin Origin,
    ImageSplitHint SplitHint,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    Guid? GenerationRunId,
    ImageGenerationState? GenerationState,
    long ImageCount,
    long ArchivedImageCount);

/// <summary>
/// Frame origin. SourceFamilyId joins frames of one source so that a dataset does not spread them across different splits:
/// <c>file:&lt;pixelHash&gt;</c>, <c>video:&lt;video hash&gt;</c>. Video and generation fields are filled only for their own kind.
/// </summary>
public sealed record ImageLineage(
    ImageLineageKind Kind,
    string SourceFamilyId,
    string? VideoName = null,
    long? TimestampMs = null,
    long? FrameIndex = null,
    Guid? GenerationRunId = null,
    int? SampleIndex = null,
    long? RecipeRevision = null);

/// <summary>
/// Group image. The file is stored unchanged in files/images/&lt;id&gt;.&lt;ext&gt;. Width/Height — with
/// EXIF orientation applied; annotation coordinates refer to the oriented frame. Sha256 — file hash, PixelHash — hash
/// of the oriented RGBA pixels (duplicates in the group). Revision grows on saving the annotation, archiving and moving;
/// AnnotationRevision — number of the current annotation revision. ObjectCount and ReviewState — from the current annotation.
/// </summary>
public sealed record ProjectImage(
    Guid Id,
    Guid GroupId,
    string DisplayName,
    ImageFileFormat Format,
    int Width,
    int Height,
    long ByteSize,
    string Sha256,
    string PixelHash,
    long Revision,
    long AnnotationRevision,
    int ObjectCount,
    ImageReviewState ReviewState,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ArchivedAt,
    string OperationId,
    ImageLineage Lineage)
{
    /// <summary>Material kind in the resource channel: <c>new ResourceReference(ResourceKind, image.Id)</c>.</summary>
    public const string ResourceKind = "images";
}

/// <summary>Box as fractions 0–1 of the frame from the top-left corner of the oriented image.</summary>
public sealed record ImageBox(double X, double Y, double Width, double Height);

/// <summary>Annotation object. ClassVariantId — a class variant (binding) of this class or null ("No variant").</summary>
public sealed record AnnotationObject(Guid Id, Guid ClassId, Guid? ClassVariantId, ImageBox Box);

/// <summary>Immutable annotation revision of an image. Roi — one area box or null.</summary>
public sealed record ImageAnnotation(
    Guid ImageId,
    long Revision,
    IReadOnlyList<AnnotationObject> Objects,
    ImageBox? Roi,
    ImageReviewState ReviewState,
    DateTimeOffset CreatedAt);

/// <summary>Object to save: Id — an existing object of the current revision or null (Core assigns the ID).</summary>
public sealed record AnnotationObjectInput(Guid? Id, Guid ClassId, Guid? ClassVariantId, ImageBox Box);

public sealed record AnnotationInput(IReadOnlyList<AnnotationObjectInput> Objects, ImageBox? Roi, ImageReviewState ReviewState);

/// <summary>Annotation save result: the image with new revisions and the new annotation revision.</summary>
public sealed record SavedAnnotation(ProjectImage Image, ImageAnnotation Annotation);

/// <summary>Page filter: Archived — null all, true only archived, false only active; ReviewState — null any.</summary>
public sealed record ImageFilter(bool? Archived = false, ImageReviewState? ReviewState = null);

/// <summary>Page of group images in import order; NextCursor — null on the last page.</summary>
public sealed record ImagePage(IReadOnlyList<ProjectImage> Items, string? NextCursor);

/// <summary>Image with the revision the user saw.</summary>
public sealed record ImageRevision(Guid Id, long Revision);

/// <summary>
/// What a permanent deletion removes: the whole group (WholeGroup) or the selected images. Revision is passed to the deletion; non-empty
/// Blockers — the deletion is unavailable now (<c>images.import_active</c>, later generation).
/// </summary>
public sealed record ImageDeletionReview(
    Guid GroupId,
    string GroupName,
    bool WholeGroup,
    long Images,
    IReadOnlyList<DeletionBlocker> Blockers,
    string Revision);

/// <summary>Rows are deleted; CleanupPending — files are still being deleted in the background.</summary>
public sealed record ImageDeletionResult(Guid GroupId, bool GroupDeleted, long Images, bool CleanupPending);

/// <summary>Move result: both groups with new revisions and the moved images.</summary>
public sealed record MovedImages(ImageGroup Source, ImageGroup Target, IReadOnlyList<ProjectImage> Images);
