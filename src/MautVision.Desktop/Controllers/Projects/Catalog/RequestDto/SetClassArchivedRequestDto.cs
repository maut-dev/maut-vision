namespace MautVision.Desktop.Controllers.Projects.Catalog.RequestDto;

/// <summary>Revision — ревизия объекта, которую видел пользователь; при расхождении — *.revision_conflict.</summary>
public sealed record SetClassArchivedRequestDto(
    string ProjectId,
    Guid ClassId,
    long Revision,
    bool Archived);
