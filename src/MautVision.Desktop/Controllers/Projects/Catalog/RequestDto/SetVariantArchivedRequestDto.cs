namespace MautVision.Desktop.Controllers.Projects.Catalog.RequestDto;

/// <summary>Revision — ревизия объекта, которую видел пользователь; при расхождении — *.revision_conflict.</summary>
public sealed record SetVariantArchivedRequestDto(
    string ProjectId,
    Guid VariantId,
    long Revision,
    bool Archived);
