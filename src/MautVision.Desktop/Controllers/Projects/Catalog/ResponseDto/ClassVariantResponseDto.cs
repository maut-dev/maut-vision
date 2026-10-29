namespace MautVision.Desktop.Controllers.Projects.Catalog.ResponseDto;

/// <summary>
/// Class variant (a binding of a project variant to a class); the name and description belong to the project variant VariantId.
/// Color — the saved #rrggbb or null; DisplayColor — the color to display (saved or default).
/// Available — neither the binding, nor the class, nor the project variant is archived. OpaqueAssetCount — images without an alpha channel;
/// MaxAssetWidth/Height — the largest image sides (0 if there are none).
/// </summary>
public sealed record ClassVariantResponseDto(
    Guid Id,
    Guid ClassId,
    Guid VariantId,
    string? Color,
    string DisplayColor,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    long Revision,
    bool Available,
    long AssetCount,
    long OpaqueAssetCount,
    int MaxAssetWidth,
    int MaxAssetHeight);
