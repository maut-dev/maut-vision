namespace MautVision.Desktop.Controllers.Projects.Images.RequestDto;

/// <summary>Up to 400 images of group GroupId.</summary>
public sealed record ReviewImagesDeletionRequestDto(string ProjectId, Guid GroupId, Guid[] ImageIds);
