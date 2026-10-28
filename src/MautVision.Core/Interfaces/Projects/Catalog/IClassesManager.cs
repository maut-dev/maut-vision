namespace MautVision.Core.Interfaces.Projects.Catalog;

/// <summary>
/// Классы проекта, их варианты (привязки вариантов проекта) и исходные изображения. Работают только у открытого
/// проекта, иначе CoreException <c>classes.project_not_open</c>. Изменения сверяют ревизию объекта
/// (<c>classes.revision_conflict</c>) и сообщаются событием <see cref="Projects.IProjectsManager.CatalogChanged"/>.
/// Отсутствующий класс, привязка или изображение — <c>classes.not_found</c>.
/// Изображения добавляются задачей <see cref="Jobs.IImportClassAssetsJob"/>.
/// </summary>
public interface IClassesManager
{
    /// <summary>Варианты, классы и привязки одним согласованным снимком, без изображений.</summary>
    Task<CatalogWorkspace> GetWorkspaceAsync(CancellationToken cancellationToken = default);

    /// <summary>Новый класс встаёт в конец порядка.</summary>
    Task<CatalogClass> CreateAsync(string name, string description, CancellationToken cancellationToken = default);

    Task<CatalogClass> UpdateAsync(Guid classId, string name, string description, long expectedRevision,
        CancellationToken cancellationToken = default);

    /// <summary>Архив действует только на сам класс и не меняет его место в порядке.</summary>
    Task<CatalogClass> SetArchivedAsync(Guid classId, bool archived, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>
    /// Новый порядок — полная перестановка всех классов проекта, включая архивные (<c>classes.order_invalid</c>).
    /// expectedOrderRevision — ревизия порядка из снимка (<c>classes.order_revision_conflict</c>).
    /// </summary>
    Task<ClassOrder> ReorderAsync(IReadOnlyList<Guid> classIds, long expectedOrderRevision, CancellationToken cancellationToken = default);

    Task<DeletionReview> ReviewDeletionAsync(Guid classId, CancellationToken cancellationToken = default);

    /// <summary>Удаляет класс, его привязки и изображения; ошибки — <c>classes.deletion_revision_conflict</c>, <c>classes.deletion_blocked</c>.</summary>
    Task<DeletionResult> DeleteAsync(Guid classId, string reviewRevision, CancellationToken cancellationToken = default);

    Task<ClassVariant> GetVariantAsync(Guid classVariantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Подключает вариант проекта к классу. Класс должен быть активным (<c>classes.class_unavailable</c>), вариант проекта —
    /// активным (<c>classes.project_variant_unavailable</c>); вторая привязка того же варианта — <c>classes.duplicate_variant_binding</c>.
    /// </summary>
    Task<ClassVariant> AddVariantAsync(Guid classId, Guid variantId, CancellationToken cancellationToken = default);

    /// <summary>Цвет <c>#rrggbb</c> (<c>classes.color_invalid</c>) или null — цвет по умолчанию.</summary>
    Task<ClassVariant> SetVariantColorAsync(Guid classVariantId, string? color, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>
    /// Восстановление требует активных класса (<c>classes.class_unavailable</c>) и варианта проекта
    /// (<c>classes.project_variant_unavailable</c>).
    /// </summary>
    Task<ClassVariant> SetVariantArchivedAsync(Guid classVariantId, bool archived, long expectedRevision, CancellationToken cancellationToken = default);

    Task<DeletionReview> ReviewVariantDeletionAsync(Guid classVariantId, CancellationToken cancellationToken = default);

    Task<DeletionResult> DeleteVariantAsync(Guid classVariantId, string reviewRevision, CancellationToken cancellationToken = default);

    /// <summary>Изображения привязки по порядку добавления; limit 1–500, after — NextCursor предыдущей страницы.</summary>
    Task<ClassAssetPage> ListAssetsAsync(Guid classVariantId, int limit = 100, string? after = null, CancellationToken cancellationToken = default);

    Task<DeletionReview> ReviewAssetDeletionAsync(Guid assetId, CancellationToken cancellationToken = default);

    Task<DeletionResult> DeleteAssetAsync(Guid assetId, string reviewRevision, CancellationToken cancellationToken = default);
}
