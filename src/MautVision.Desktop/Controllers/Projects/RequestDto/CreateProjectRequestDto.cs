namespace MautVision.Desktop.Controllers.Projects.RequestDto;

public sealed record CreateProjectRequestDto(string Name, string Description, string BaseModelId, InputSizeDto InputSize);
