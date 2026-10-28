namespace MautVision.Core.Interfaces.Projects.Catalog;

/// <summary>
/// A project-wide display variant of an object. The name is unique case-insensitively among the project variants,
/// including archived ones. Revision grows with every saved change.
/// </summary>
public sealed record ProjectVariant(
    Guid Id,
    string Name,
    string Description,
    long SortOrder,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    long Revision);

/// <summary>
/// Class — a recognition object. SortOrder sets the class order: when a dataset is built, the selected classes get
/// indexes from 0 without gaps in this order. The order changes only by a reordering with the order revision.
/// </summary>
public sealed record CatalogClass(
    Guid Id,
    string Name,
    string Description,
    long SortOrder,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    long Revision);

/// <summary>
/// Class variant — a binding of a project variant to a class; the name and description come from the project variant.
/// Color — the saved color <c>#rrggbb</c> or null (default color, <see cref="DisplayColor"/>).
/// Available — neither the binding, nor its class, nor the project variant is archived. Image counters are stored in the binding.
/// </summary>
public sealed record ClassVariant(
    Guid Id,
    Guid ClassId,
    Guid VariantId,
    string? Color,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    long Revision,
    bool Available,
    long AssetCount,
    long OpaqueAssetCount,
    int MaxAssetWidth,
    int MaxAssetHeight)
{
    public string DisplayColor => Color ?? VariantColors.Default(Id);
}

public enum ClassAssetFormat
{
    Png,
    Jpeg,
    Webp,
    Bmp,
}

/// <summary>
/// Source image of a class variant. Bytes are stored unchanged in files/class-assets/&lt;id&gt;.&lt;ext&gt;.
/// Width and Height — size with EXIF orientation applied; HasAlpha — the file has an alpha channel.
/// </summary>
public sealed record ClassAsset(
    Guid Id,
    Guid ClassVariantId,
    ClassAssetFormat Format,
    int Width,
    int Height,
    bool HasAlpha,
    long ByteSize,
    string Sha256,
    DateTimeOffset CreatedAt,
    string OperationId)
{
    /// <summary>Material kind in the resource channel: <c>new ResourceReference(ResourceKind, asset.Id)</c>.</summary>
    public const string ResourceKind = "class-assets";
}

/// <summary>
/// Lightweight catalog snapshot for the screen: variants and classes in SortOrder, bindings with counters, without images.
/// CatalogRevision grows with any change of the project catalog; a snapshot with a lower value is stale.
/// </summary>
public sealed record CatalogWorkspace(
    IReadOnlyList<ProjectVariant> Variants,
    IReadOnlyList<CatalogClass> Classes,
    IReadOnlyList<ClassVariant> ClassVariants,
    long ClassOrderRevision,
    long CatalogRevision);

/// <summary>Page of binding images in the order of adding; NextCursor — null on the last page.</summary>
public sealed record ClassAssetPage(IReadOnlyList<ClassAsset> Items, string? NextCursor);

public sealed record ClassOrder(IReadOnlyList<CatalogClass> Classes, long OrderRevision);

public enum CatalogTargetKind
{
    Variant,
    Class,
    ClassVariant,
    Asset,
}

/// <summary>
/// Reason why a deletion is unavailable now: Code — a stable code (for example, <c>jobs.active</c>),
/// Count — number of blocking objects, Examples — a few names for display.
/// </summary>
public sealed record DeletionBlocker(string Code, int Count, IReadOnlyList<string> Examples);

/// <summary>
/// What a deletion removes. Revision describes the target and all rows deleted with it; the deletion checks it in a transaction.
/// TargetName — name of the variant, class or project variant of the binding; empty for an image.
/// </summary>
public sealed record DeletionReview(
    CatalogTargetKind TargetKind,
    Guid TargetId,
    string TargetName,
    long Classes,
    long ClassVariants,
    long Assets,
    IReadOnlyList<DeletionBlocker> Blockers,
    string Revision);

/// <summary>
/// Rows are already deleted. CleanupPending — files are still being deleted in the background (there are many or some are locked); the cleanup continues
/// on the next deletion and when the project is opened.
/// </summary>
public sealed record DeletionResult(long Classes, long ClassVariants, long Assets, bool CleanupPending);
