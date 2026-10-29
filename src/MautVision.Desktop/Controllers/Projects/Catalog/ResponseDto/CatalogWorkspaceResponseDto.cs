namespace MautVision.Desktop.Controllers.Projects.Catalog.ResponseDto;

/// <summary>
/// Catalog snapshot without images: variants and classes in SortOrder (including archived ones), bindings.
/// CatalogRevision grows with every catalog change; a snapshot or event with a lower value is stale.
/// </summary>
public sealed record CatalogWorkspaceResponseDto(
    VariantResponseDto[] Variants,
    ClassResponseDto[] Classes,
    ClassVariantResponseDto[] ClassVariants,
    long ClassOrderRevision,
    long CatalogRevision);

public sealed record ClassOrderResponseDto(ClassResponseDto[] Classes, long OrderRevision);
