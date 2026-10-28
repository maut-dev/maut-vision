namespace MautVision.Core.Interfaces.Projects.Catalog;

/// <summary>Changed or deleted (Revision = null) catalog object.</summary>
public sealed record CatalogEntityChange(Guid Id, long? Revision);

/// <summary>
/// What changed in the project catalog. AssetsChangedIn — bindings whose image list (and its counters) changed.
/// CatalogRevision grows with every catalog change: an event with a lower value than an already known one is stale.
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
