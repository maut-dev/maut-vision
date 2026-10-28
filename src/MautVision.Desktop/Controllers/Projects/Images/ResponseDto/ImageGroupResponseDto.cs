namespace MautVision.Desktop.Controllers.Projects.Images.ResponseDto;

/// <summary>
/// Группа изображений. Origin: manual, generated. SplitHint: unassigned, train, validation, test. GenerationState: producing,
/// completed, partial или null (ручная группа). ImageCount — все изображения, включая архивные; ArchivedImageCount — архивные.
/// Revision растёт при изменении полей группы, архиве и перемещении изображений; импорт меняет только счётчики.
/// </summary>
public sealed record ImageGroupResponseDto(
    Guid Id,
    string Name,
    string Description,
    string Origin,
    string SplitHint,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    Guid? GenerationRunId,
    string? GenerationState,
    long ImageCount,
    long ArchivedImageCount);
