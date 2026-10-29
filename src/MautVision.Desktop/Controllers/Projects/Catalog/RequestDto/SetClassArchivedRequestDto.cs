namespace MautVision.Desktop.Controllers.Projects.Catalog.RequestDto;

/// <summary>Revision — the object revision the user saw; on a mismatch — *.revision_conflict.</summary>
public sealed record SetClassArchivedRequestDto(
    string ProjectId,
    Guid ClassId,
    long Revision,
    bool Archived);
