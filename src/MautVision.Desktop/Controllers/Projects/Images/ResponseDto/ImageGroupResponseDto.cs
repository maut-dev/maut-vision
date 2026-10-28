namespace MautVision.Desktop.Controllers.Projects.Images.ResponseDto;

/// <summary>
/// Image group. Origin: manual, generated. SplitHint: unassigned, train, validation, test. GenerationState: producing,
/// completed, partial or null (manual group). ImageCount — all images, including archived ones; ArchivedImageCount — archived ones.
/// Revision grows on a change of group fields, archiving and moving images; import changes only the counters.
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
