namespace MautVision.Desktop.Controllers.Projects.Backgrounds.RequestDto;

/// <summary>Preparation history of a set; Limit — 1–200 (default 50), Before — ID of the last job of the previous page.</summary>
public sealed record ListBackgroundRunsRequestDto(string ProjectId, Guid SetId, int? Limit = null, Guid? Before = null);
