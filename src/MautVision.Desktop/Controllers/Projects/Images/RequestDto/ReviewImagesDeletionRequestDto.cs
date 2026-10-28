namespace MautVision.Desktop.Controllers.Projects.Images.RequestDto;

/// <summary>До 400 изображений группы GroupId.</summary>
public sealed record ReviewImagesDeletionRequestDto(string ProjectId, Guid GroupId, Guid[] ImageIds);
