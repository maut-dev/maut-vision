namespace MautVision.Desktop.Controllers.Projects.Images.RequestDto;

/// <summary>
/// Archive: active (по умолчанию), archived, all. ReviewState: unreviewed, reviewed или null — любые. Limit — 1–200, по умолчанию 100.
/// After — nextCursor предыдущей страницы того же фильтра.
/// </summary>
public sealed record ListImagesRequestDto(
    string ProjectId,
    Guid GroupId,
    string? Archive = null,
    string? ReviewState = null,
    int? Limit = null,
    string? After = null);
