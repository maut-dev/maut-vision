namespace MautVision.Desktop.Controllers.Projects.Backgrounds.RequestDto;

/// <summary>ReviewRevision — revision from backgrounds.reviewSetDeletion.</summary>
public sealed record DeleteBackgroundSetRequestDto(string ProjectId, Guid SetId, string ReviewRevision);
