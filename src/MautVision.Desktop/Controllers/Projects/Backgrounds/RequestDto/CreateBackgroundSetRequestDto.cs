namespace MautVision.Desktop.Controllers.Projects.Backgrounds.RequestDto;

/// <summary>Название 1–120 символов, описание до 4000; размер набора — текущий размер входа проекта.</summary>
public sealed record CreateBackgroundSetRequestDto(string ProjectId, string Name, string Description);
