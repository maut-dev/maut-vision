namespace MautVision.Core.Interfaces.Projects.Catalog;

/// <summary>Изменённый или удалённый (Revision = null) объект каталога.</summary>
public sealed record CatalogEntityChange(Guid Id, long? Revision);

/// <summary>
/// Что изменилось в каталоге проекта. AssetsChangedIn — привязки, у которых изменился список изображений (и их счётчики).
/// CatalogRevision растёт с каждым изменением каталога: событие с меньшим значением, чем уже известное, устарело.
/// </summary>
public sealed class CatalogChangedEventArgs(
    string projectId,
    IReadOnlyList<CatalogEntityChange> variants,
    IReadOnlyList<CatalogEntityChange> classes,
    IReadOnlyList<CatalogEntityChange> classVariants,
    IReadOnlyList<Guid> assetsChangedIn,
    long classOrderRevision,
    long catalogRevision) : EventArgs
{
    public string ProjectId { get; } = projectId;
    public IReadOnlyList<CatalogEntityChange> Variants { get; } = variants;
    public IReadOnlyList<CatalogEntityChange> Classes { get; } = classes;
    public IReadOnlyList<CatalogEntityChange> ClassVariants { get; } = classVariants;
    public IReadOnlyList<Guid> AssetsChangedIn { get; } = assetsChangedIn;
    public long ClassOrderRevision { get; } = classOrderRevision;
    public long CatalogRevision { get; } = catalogRevision;
}
