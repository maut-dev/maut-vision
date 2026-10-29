namespace MautVision.Desktop.Controllers.Projects.Images.RequestDto;

/// <summary>
/// Archive: active (default), archived, all. ReviewState: unreviewed, reviewed or null — any. Limit — 1–200, default 100.
/// After — nextCursor of the previous page of the same filter.
/// </summary>
public sealed record ListImagesRequestDto(
    string ProjectId,
    Guid GroupId,
    string? Archive = null,
    string? ReviewState = null,
    int? Limit = null,
    string? After = null);
