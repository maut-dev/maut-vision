namespace MautVision.Desktop.Controllers.Projects.Backgrounds.RequestDto;

/// <summary>Revision — the set revision the user saw.</summary>
public sealed record UpdateBackgroundSetRequestDto(string ProjectId, Guid SetId, string Name, string Description, long Revision);
