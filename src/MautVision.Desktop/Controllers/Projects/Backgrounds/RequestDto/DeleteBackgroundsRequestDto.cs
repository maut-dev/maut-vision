namespace MautVision.Desktop.Controllers.Projects.Backgrounds.RequestDto;

/// <summary>ReviewRevision — revision из backgrounds.reviewDeletion для тех же ItemIds.</summary>
public sealed record DeleteBackgroundsRequestDto(string ProjectId, Guid SetId, Guid[] ItemIds, string ReviewRevision);
