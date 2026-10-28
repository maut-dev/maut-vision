namespace MautVision.Desktop.Controllers.Projects.Images.RequestDto;

public sealed record DeleteImagesRequestDto(string ProjectId, Guid GroupId, Guid[] ImageIds, string ReviewRevision);
