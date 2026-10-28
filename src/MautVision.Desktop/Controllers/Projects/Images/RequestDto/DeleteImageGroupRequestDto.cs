namespace MautVision.Desktop.Controllers.Projects.Images.RequestDto;

public sealed record DeleteImageGroupRequestDto(string ProjectId, Guid GroupId, string ReviewRevision);
