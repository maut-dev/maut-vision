namespace MautVision.Desktop.Controllers.Projects.Catalog.RequestDto;

/// <summary>VariantId — ID of the project variant.</summary>
public sealed record AddClassVariantRequestDto(
    string ProjectId,
    Guid ClassId,
    Guid VariantId);
