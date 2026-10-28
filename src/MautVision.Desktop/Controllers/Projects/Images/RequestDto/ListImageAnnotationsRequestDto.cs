namespace MautVision.Desktop.Controllers.Projects.Images.RequestDto;

/// <summary>Up to 200 images, usually one gallery page.</summary>
public sealed record ListImageAnnotationsRequestDto(string ProjectId, Guid[] ImageIds);
