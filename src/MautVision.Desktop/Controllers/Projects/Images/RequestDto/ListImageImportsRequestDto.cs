namespace MautVision.Desktop.Controllers.Projects.Images.RequestDto;

/// <summary>Limit — 1–200, по умолчанию 50; Before — ID последней задачи предыдущей страницы.</summary>
public sealed record ListImageImportsRequestDto(string ProjectId, Guid GroupId, int? Limit = null, Guid? Before = null);
