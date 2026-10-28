namespace MautVision.Desktop.Controllers.Projects.Backgrounds.RequestDto;

/// <summary>
/// Removed: active (default), removed ("Show removed backgrounds" — only removed ones), all. Limit — 1–200, default 100.
/// After — nextCursor of the previous page of the same filter.
/// </summary>
public sealed record ListBackgroundsRequestDto(string ProjectId, Guid SetId, string? Removed = null, int? Limit = null, string? After = null);
