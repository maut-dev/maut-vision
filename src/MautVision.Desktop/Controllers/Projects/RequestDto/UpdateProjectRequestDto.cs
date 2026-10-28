namespace MautVision.Desktop.Controllers.Projects.RequestDto;

/// <summary>Revision — значение, которое видел пользователь; при расхождении изменение отклоняется.</summary>
public sealed record UpdateProjectRequestDto(
    string ProjectId,
    long Revision,
    string Name,
    string Description,
    string BaseModelId,
    InputSizeDto InputSize);
