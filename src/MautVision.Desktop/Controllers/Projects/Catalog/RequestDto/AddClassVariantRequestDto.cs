namespace MautVision.Desktop.Controllers.Projects.Catalog.RequestDto;

/// <summary>VariantId — ID варианта проекта.</summary>
public sealed record AddClassVariantRequestDto(
    string ProjectId,
    Guid ClassId,
    Guid VariantId);
