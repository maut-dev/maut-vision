namespace MautVision.Desktop.Controllers.Projects.Backgrounds.RequestDto;

/// <summary>
/// Removed: active (по умолчанию), removed («Показать убранные фоны» — только убранные), all. Limit — 1–200, по умолчанию 100.
/// After — nextCursor предыдущей страницы того же фильтра.
/// </summary>
public sealed record ListBackgroundsRequestDto(string ProjectId, Guid SetId, string? Removed = null, int? Limit = null, string? After = null);
