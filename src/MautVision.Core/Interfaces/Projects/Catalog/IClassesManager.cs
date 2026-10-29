namespace MautVision.Core.Interfaces.Projects.Catalog;

/// <summary>
/// Project classes, their variants (bindings of project variants) and source images. Work only for an open
/// project, otherwise CoreException <c>classes.project_not_open</c>. Changes check the object revision
/// (<c>classes.revision_conflict</c>) and are reported by the event <see cref="Projects.IProjectsManager.CatalogChanged"/>.
/// Missing class, binding or image — <c>classes.not_found</c>.
/// Images are added by the job <see cref="Jobs.IImportClassAssetsJob"/>.
/// </summary>
public interface IClassesManager
{
    /// <summary>Variants, classes and bindings in one consistent snapshot, without images.</summary>
    Task<CatalogWorkspace> GetWorkspaceAsync(CancellationToken cancellationToken = default);

    /// <summary>A new class goes to the end of the order.</summary>
    Task<CatalogClass> CreateAsync(string name, string description, CancellationToken cancellationToken = default);

    Task<CatalogClass> UpdateAsync(Guid classId, string name, string description, long expectedRevision,
        CancellationToken cancellationToken = default);

    /// <summary>Archiving affects only the class itself and does not change its place in the order.</summary>
    Task<CatalogClass> SetArchivedAsync(Guid classId, bool archived, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>
    /// The new order is a full permutation of all project classes, including archived ones (<c>classes.order_invalid</c>).
    /// expectedOrderRevision — the order revision from the snapshot (<c>classes.order_revision_conflict</c>).
    /// </summary>
    Task<ClassOrder> ReorderAsync(IReadOnlyList<Guid> classIds, long expectedOrderRevision, CancellationToken cancellationToken = default);

    Task<DeletionReview> ReviewDeletionAsync(Guid classId, CancellationToken cancellationToken = default);

    /// <summary>Deletes the class, its bindings and images; errors — <c>classes.deletion_revision_conflict</c>, <c>classes.deletion_blocked</c>.</summary>
    Task<DeletionResult> DeleteAsync(Guid classId, string reviewRevision, CancellationToken cancellationToken = default);

    Task<ClassVariant> GetVariantAsync(Guid classVariantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attaches a project variant to a class. The class must be active (<c>classes.class_unavailable</c>), the project variant must be
    /// active (<c>classes.project_variant_unavailable</c>); a second binding of the same variant — <c>classes.duplicate_variant_binding</c>.
    /// </summary>
    Task<ClassVariant> AddVariantAsync(Guid classId, Guid variantId, CancellationToken cancellationToken = default);

    /// <summary>Color <c>#rrggbb</c> (<c>classes.color_invalid</c>) or null — the default color.</summary>
    Task<ClassVariant> SetVariantColorAsync(Guid classVariantId, string? color, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restoring requires an active class (<c>classes.class_unavailable</c>) and an active project variant
    /// (<c>classes.project_variant_unavailable</c>).
    /// </summary>
    Task<ClassVariant> SetVariantArchivedAsync(Guid classVariantId, bool archived, long expectedRevision, CancellationToken cancellationToken = default);

    Task<DeletionReview> ReviewVariantDeletionAsync(Guid classVariantId, CancellationToken cancellationToken = default);

    Task<DeletionResult> DeleteVariantAsync(Guid classVariantId, string reviewRevision, CancellationToken cancellationToken = default);

    /// <summary>Binding images in the order of adding; limit 1–500, after — NextCursor of the previous page.</summary>
    Task<ClassAssetPage> ListAssetsAsync(Guid classVariantId, int limit = 100, string? after = null, CancellationToken cancellationToken = default);

    Task<DeletionReview> ReviewAssetDeletionAsync(Guid assetId, CancellationToken cancellationToken = default);

    Task<DeletionResult> DeleteAssetAsync(Guid assetId, string reviewRevision, CancellationToken cancellationToken = default);
}
