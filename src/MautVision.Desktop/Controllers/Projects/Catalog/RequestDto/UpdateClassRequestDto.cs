namespace MautVision.Desktop.Controllers.Projects.Catalog.RequestDto;

/// <summary>Revision — ревизия объекта, которую видел пользователь; при расхождении — *.revision_conflict.</summary>
public sealed record UpdateClassRequestDto(
    string ProjectId,
    Guid ClassId,
    long Revision,
    string Name,
    string Description);
