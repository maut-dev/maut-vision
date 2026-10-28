namespace MautVision.Desktop.Controllers.Projects.Catalog.RequestDto;

/// <summary>Revision — ревизия объекта, которую видел пользователь; при расхождении — *.revision_conflict.</summary>
public sealed record SetClassVariantArchivedRequestDto(
    string ProjectId,
    Guid ClassVariantId,
    long Revision,
    bool Archived);
