namespace MautVision.Desktop.Controllers.Projects.Backgrounds.RequestDto;

/// <summary>ReviewRevision — revision from backgrounds.reviewDeletion for the same ItemIds.</summary>
public sealed record DeleteBackgroundsRequestDto(string ProjectId, Guid SetId, Guid[] ItemIds, string ReviewRevision);
