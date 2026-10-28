namespace MautVision.Desktop.Controllers.Projects.ResponseDto;

public sealed record ProjectResponseDto(
    string Id,
    string Name,
    string Description,
    string BaseModelId,
    InputSizeDto InputSize,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt);
