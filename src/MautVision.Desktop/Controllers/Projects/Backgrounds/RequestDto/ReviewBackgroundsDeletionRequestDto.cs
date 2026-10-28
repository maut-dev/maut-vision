namespace MautVision.Desktop.Controllers.Projects.Backgrounds.RequestDto;

/// <summary>До 400 фонов одного набора.</summary>
public sealed record ReviewBackgroundsDeletionRequestDto(string ProjectId, Guid SetId, Guid[] ItemIds);
