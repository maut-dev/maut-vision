namespace MautVision.Desktop.Controllers.Projects.Images.RequestDto;

/// <summary>Revision — ревизия группы, которую видел пользователь. SplitHint: unassigned, train, validation, test.</summary>
public sealed record UpdateImageGroupRequestDto(string ProjectId, Guid GroupId, string Name, string Description, string SplitHint, long Revision);
