namespace MautVision.Core.Interfaces.Projects.Catalog;

/// <summary>
/// Project variants. Work only for an open project (<see cref="Projects.IProjectsManager.OpenAsync"/>), otherwise
/// CoreException <c>variants.project_not_open</c>. Changes check the object revision (<c>variants.revision_conflict</c>)
/// and are reported by the event <see cref="Projects.IProjectsManager.CatalogChanged"/>.
/// Data errors: <c>variants.name_invalid</c>, <c>variants.description_invalid</c>, <c>variants.name_duplicate</c>,
/// <c>variants.not_found</c>.
/// </summary>
public interface IVariantsManager
{
    /// <summary>All variants, including archived ones, in SortOrder.</summary>
    Task<IReadOnlyList<ProjectVariant>> ListAsync(CancellationToken cancellationToken = default);

    Task<ProjectVariant> CreateAsync(string name, string description, CancellationToken cancellationToken = default);

    Task<ProjectVariant> UpdateAsync(Guid variantId, string name, string description, long expectedRevision,
        CancellationToken cancellationToken = default);

    /// <summary>Archiving affects only the variant itself; bindings with it become unavailable but do not change.</summary>
    Task<ProjectVariant> SetArchivedAsync(Guid variantId, bool archived, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>What a deletion removes: bindings in all classes and their images, blocking reasons and the content revision.</summary>
    Task<DeletionReview> ReviewDeletionAsync(Guid variantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the variant, its bindings and images if the content matches reviewRevision
    /// (otherwise <c>variants.deletion_revision_conflict</c>) and there are no blocking reasons (<c>variants.deletion_blocked</c>).
    /// </summary>
    Task<DeletionResult> DeleteAsync(Guid variantId, string reviewRevision, CancellationToken cancellationToken = default);
}
