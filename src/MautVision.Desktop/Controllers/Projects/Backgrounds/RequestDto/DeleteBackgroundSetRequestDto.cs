namespace MautVision.Desktop.Controllers.Projects.Backgrounds.RequestDto;

/// <summary>ReviewRevision — revision из backgrounds.reviewSetDeletion.</summary>
public sealed record DeleteBackgroundSetRequestDto(string ProjectId, Guid SetId, string ReviewRevision);
