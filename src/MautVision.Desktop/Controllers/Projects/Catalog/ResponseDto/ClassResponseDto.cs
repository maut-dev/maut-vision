namespace MautVision.Desktop.Controllers.Projects.Catalog.ResponseDto;

/// <summary>Класс. SortOrder — порядок классов: при сборке выбранные классы получают индексы от 0 в этом порядке.</summary>
public sealed record ClassResponseDto(
    Guid Id,
    string Name,
    string Description,
    long SortOrder,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    long Revision);
