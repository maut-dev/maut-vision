namespace MautVision.Desktop.Controllers.Projects.Backgrounds.RequestDto;

/// <summary>Name 1–120 characters, description up to 4000; the set size is the current project input size.</summary>
public sealed record CreateBackgroundSetRequestDto(string ProjectId, string Name, string Description);
