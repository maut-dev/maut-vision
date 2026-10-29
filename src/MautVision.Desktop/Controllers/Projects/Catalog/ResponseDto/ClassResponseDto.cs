namespace MautVision.Desktop.Controllers.Projects.Catalog.ResponseDto;

/// <summary>Class. SortOrder — class order: when building, the selected classes get indexes from 0 in this order.</summary>
public sealed record ClassResponseDto(
    Guid Id,
    string Name,
    string Description,
    long SortOrder,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    long Revision);
