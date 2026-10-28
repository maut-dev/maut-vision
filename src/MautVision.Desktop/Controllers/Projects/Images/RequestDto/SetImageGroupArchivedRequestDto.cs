namespace MautVision.Desktop.Controllers.Projects.Images.RequestDto;

public sealed record SetImageGroupArchivedRequestDto(string ProjectId, Guid GroupId, bool Archived, long Revision);
