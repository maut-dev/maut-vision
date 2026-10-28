namespace MautVision.Desktop.Controllers.Projects.Catalog.ResponseDto;

/// <summary>Изменённый объект с новой ревизией или удалённый (Revision = null).</summary>
public sealed record CatalogEntityChangeDto(Guid Id, long? Revision);

/// <summary>
/// Событие catalog.changed. AssetsChangedIn — привязки, у которых изменились изображения и счётчики.
/// UI перечитывает снимок каталога, если CatalogRevision больше известного, и страницы изображений этих привязок.
/// </summary>
public sealed record CatalogChangedEventDto(
    string ProjectId,
    CatalogEntityChangeDto[] Variants,
    CatalogEntityChangeDto[] Classes,
    CatalogEntityChangeDto[] ClassVariants,
    Guid[] AssetsChangedIn,
    long ClassOrderRevision,
    long CatalogRevision);
