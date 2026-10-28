namespace MautVision.Desktop.Controllers.Projects.Catalog.RequestDto;

/// <summary>Revision — the object revision the user saw; on a mismatch — *.revision_conflict. Color — #rrggbb or null for the default color.</summary>
public sealed record SetClassVariantColorRequestDto(
    string ProjectId,
    Guid ClassVariantId,
    long Revision,
    string? Color);
