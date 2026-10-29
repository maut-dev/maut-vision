namespace MautVision.Desktop.Controllers.Projects.Images.RequestDto;

/// <summary>Limit — 1–200, default 50; Before — ID of the last job of the previous page.</summary>
public sealed record ListImageImportsRequestDto(string ProjectId, Guid GroupId, int? Limit = null, Guid? Before = null);
