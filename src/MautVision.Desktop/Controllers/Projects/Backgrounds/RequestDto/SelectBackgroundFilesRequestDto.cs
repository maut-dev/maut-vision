namespace MautVision.Desktop.Controllers.Projects.Backgrounds.RequestDto;

/// <summary>Native file selection dialog; the answer is a selection token and file headers relative to the size of set SetId.</summary>
public sealed record SelectBackgroundFilesRequestDto(string ProjectId, Guid SetId);
