namespace MautVision.Desktop.Controllers.Projects.Catalog.ResponseDto;

/// <summary>A changed object with a new revision or a deleted one (Revision = null).</summary>
public sealed record CatalogEntityChangeDto(Guid Id, long? Revision);

/// <summary>
/// The catalog.changed event. AssetsChangedIn — bindings whose images and counters changed.
/// The UI rereads the catalog snapshot if CatalogRevision is greater than the known one, and the image pages of these bindings.
/// </summary>
public sealed record CatalogChangedEventDto(
    string ProjectId,
    CatalogEntityChangeDto[] Variants,
    CatalogEntityChangeDto[] Classes,
    CatalogEntityChangeDto[] ClassVariants,
    Guid[] AssetsChangedIn,
    long ClassOrderRevision,
    long CatalogRevision);
