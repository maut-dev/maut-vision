namespace MautVision.Desktop.Controllers.Projects.Backgrounds.RequestDto;

/// <summary>Revision — ревизия набора, которую видел пользователь.</summary>
public sealed record UpdateBackgroundSetRequestDto(string ProjectId, Guid SetId, string Name, string Description, long Revision);
