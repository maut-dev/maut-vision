namespace MautVision.Desktop.Controllers.Projects.Images.RequestDto;

/// <summary>SplitHint: unassigned, train, validation, test.</summary>
public sealed record CreateImageGroupRequestDto(string ProjectId, string Name, string Description, string SplitHint);
