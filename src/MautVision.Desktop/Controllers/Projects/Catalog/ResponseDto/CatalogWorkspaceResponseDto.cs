namespace MautVision.Desktop.Controllers.Projects.Catalog.ResponseDto;

/// <summary>
/// Снимок каталога без изображений: варианты и классы в порядке SortOrder (включая архивные), привязки.
/// CatalogRevision растёт с каждым изменением каталога; снимок или событие с меньшим значением устарели.
/// </summary>
public sealed record CatalogWorkspaceResponseDto(
    VariantResponseDto[] Variants,
    ClassResponseDto[] Classes,
    ClassVariantResponseDto[] ClassVariants,
    long ClassOrderRevision,
    long CatalogRevision);

public sealed record ClassOrderResponseDto(ClassResponseDto[] Classes, long OrderRevision);
