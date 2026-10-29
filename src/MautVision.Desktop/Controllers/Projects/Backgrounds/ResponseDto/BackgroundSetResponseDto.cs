using MautVision.Desktop.Controllers.Projects;

namespace MautVision.Desktop.Controllers.Projects.Backgrounds.ResponseDto;

/// <summary>
/// Background set. TargetSize — background size (a copy of the project input at creation); MatchesProjectInput — false if the project input
/// changed later ("Set size W × H differs from the project input"). Revision — for editing and archiving; ContentRevision
/// grows on every content change. ItemCount — active backgrounds ("N backgrounds"), RemovedCount — removed ones.
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
