namespace MautVision.Desktop.Controllers.Projects.Backgrounds.RequestDto;

public sealed record SetBackgroundSetArchivedRequestDto(string ProjectId, Guid SetId, bool Archived, long Revision);
