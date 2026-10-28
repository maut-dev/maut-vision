using MautVision.Desktop.Controllers.Projects;

namespace MautVision.Desktop.Controllers.Projects.Backgrounds.ResponseDto;

/// <summary>
/// Набор фонов. TargetSize — размер фонов (копия входа проекта при создании); MatchesProjectInput — false, если вход проекта
/// позже изменён («Размер набора W × H отличается от входа проекта»). Revision — для изменения и архива; ContentRevision
/// растёт при каждом изменении состава. ItemCount — активные фоны («N фонов»), RemovedCount — убранные.
/// </summary>
public sealed record BackgroundSetResponseDto(
    Guid Id,
    string Name,
    string Description,
    InputSizeDto TargetSize,
    long Revision,
    long ContentRevision,
    long ItemCount,
    long RemovedCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    bool MatchesProjectInput);
