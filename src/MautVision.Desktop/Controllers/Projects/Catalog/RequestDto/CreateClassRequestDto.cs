namespace MautVision.Desktop.Controllers.Projects.Catalog.RequestDto;

public sealed record CreateClassRequestDto(
    string ProjectId,
    string Name,
    string Description);
