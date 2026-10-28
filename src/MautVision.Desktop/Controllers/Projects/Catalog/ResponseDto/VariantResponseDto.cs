namespace MautVision.Desktop.Controllers.Projects.Catalog.ResponseDto;

/// <summary>Project variant. SortOrder — order in the list; ArchivedAt — null for an active one.</summary>
public sealed record VariantResponseDto(
    Guid Id,
    string Name,
    string Description,
    long SortOrder,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    long Revision);
