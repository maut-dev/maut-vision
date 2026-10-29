namespace MautVision.Desktop.Controllers.Projects.Backgrounds.RequestDto;

/// <summary>Up to 400 backgrounds of one set.</summary>
public sealed record ReviewBackgroundsDeletionRequestDto(string ProjectId, Guid SetId, Guid[] ItemIds);
