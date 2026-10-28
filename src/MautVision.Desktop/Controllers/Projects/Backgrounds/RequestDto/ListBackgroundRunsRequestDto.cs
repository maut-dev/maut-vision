namespace MautVision.Desktop.Controllers.Projects.Backgrounds.RequestDto;

/// <summary>История подготовки набора; Limit — 1–200 (по умолчанию 50), Before — ID последней задачи предыдущей страницы.</summary>
public sealed record ListBackgroundRunsRequestDto(string ProjectId, Guid SetId, int? Limit = null, Guid? Before = null);
