namespace MautVision.Desktop.Controllers.Projects.Catalog.RequestDto;

/// <summary>ReviewRevision — revision from the deletion review answer.</summary>
public sealed record DeleteClassVariantRequestDto(
    string ProjectId,
    Guid ClassVariantId,
    string ReviewRevision);
