namespace MautVision.Desktop.Controllers.Projects.Catalog.RequestDto;

/// <summary>ReviewRevision — revision from the deletion review answer.</summary>
public sealed record DeleteVariantRequestDto(
    string ProjectId,
    Guid VariantId,
    string ReviewRevision);
