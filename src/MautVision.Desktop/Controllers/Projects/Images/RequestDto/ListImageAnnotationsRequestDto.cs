namespace MautVision.Desktop.Controllers.Projects.Images.RequestDto;

/// <summary>До 200 изображений, обычно одна страница галереи.</summary>
public sealed record ListImageAnnotationsRequestDto(string ProjectId, Guid[] ImageIds);
