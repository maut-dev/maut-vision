namespace MautVision.Desktop.Controllers.Projects.Catalog.ResponseDto;

/// <summary>Вариант проекта. SortOrder — порядок в списке; ArchivedAt — null для активного.</summary>
public sealed record VariantResponseDto(
    Guid Id,
    string Name,
    string Description,
    long SortOrder,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    long Revision);
