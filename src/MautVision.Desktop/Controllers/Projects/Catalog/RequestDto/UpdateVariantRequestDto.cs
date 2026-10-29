namespace MautVision.Desktop.Controllers.Projects.Catalog.RequestDto;

/// <summary>Revision — the object revision the user saw; on a mismatch — *.revision_conflict.</summary>
public sealed record UpdateVariantRequestDto(
    string ProjectId,
    Guid VariantId,
    long Revision,
    string Name,
    string Description);
