namespace MautVision.Core.Interfaces.Projects.Catalog;

/// <summary>
/// Варианты проекта. Работают только у открытого проекта (<see cref="Projects.IProjectsManager.OpenAsync"/>), иначе
/// CoreException <c>variants.project_not_open</c>. Изменения сверяют ревизию объекта (<c>variants.revision_conflict</c>)
/// и сообщаются событием <see cref="Projects.IProjectsManager.CatalogChanged"/>.
/// Ошибки данных: <c>variants.name_invalid</c>, <c>variants.description_invalid</c>, <c>variants.name_duplicate</c>,
/// <c>variants.not_found</c>.
/// </summary>
public interface IVariantsManager
{
    /// <summary>Все варианты, включая архивные, в порядке SortOrder.</summary>
    Task<IReadOnlyList<ProjectVariant>> ListAsync(CancellationToken cancellationToken = default);

    Task<ProjectVariant> CreateAsync(string name, string description, CancellationToken cancellationToken = default);

    Task<ProjectVariant> UpdateAsync(Guid variantId, string name, string description, long expectedRevision,
        CancellationToken cancellationToken = default);

    /// <summary>Архив действует только на сам вариант; привязки с ним становятся недоступными, но не меняются.</summary>
    Task<ProjectVariant> SetArchivedAsync(Guid variantId, bool archived, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>Состав удаления: привязки во всех классах и их изображения, блокирующие причины и ревизия состава.</summary>
    Task<DeletionReview> ReviewDeletionAsync(Guid variantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Удаляет вариант, его привязки и изображения, если состав совпадает с reviewRevision
    /// (иначе <c>variants.deletion_revision_conflict</c>) и нет блокирующих причин (<c>variants.deletion_blocked</c>).
    /// </summary>
    Task<DeletionResult> DeleteAsync(Guid variantId, string reviewRevision, CancellationToken cancellationToken = default);
}
