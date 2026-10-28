namespace MautVision.Desktop.Controllers.Projects.Images.RequestDto;

/// <summary>Revision — the group revision the user saw. SplitHint: unassigned, train, validation, test.</summary>
public sealed record UpdateImageGroupRequestDto(string ProjectId, Guid GroupId, string Name, string Description, string SplitHint, long Revision);
