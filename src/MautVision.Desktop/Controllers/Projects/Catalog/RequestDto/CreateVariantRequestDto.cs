namespace MautVision.Desktop.Controllers.Projects.Catalog.RequestDto;

public sealed record CreateVariantRequestDto(
    string ProjectId,
    string Name,
    string Description);
