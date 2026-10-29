namespace MautVision.Desktop.Controllers.Projects.RequestDto;

/// <summary>Revision — the value the user saw; on a mismatch the change is rejected.</summary>
public sealed record UpdateProjectRequestDto(
    string ProjectId,
    long Revision,
    string Name,
    string Description,
    string BaseModelId,
    InputSizeDto InputSize);
