namespace MautVision.Desktop.Controllers.Projects.Catalog.ResponseDto;

/// <summary>
/// Вариант класса (привязка варианта проекта к классу); название и описание — у варианта проекта VariantId.
/// Color — сохранённый #rrggbb или null; DisplayColor — цвет для показа (сохранённый или по умолчанию).
/// Available — ни привязка, ни класс, ни вариант проекта не в архиве. OpaqueAssetCount — изображения без альфа-канала;
/// MaxAssetWidth/Height — наибольшие стороны изображений (0, если их нет).
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
