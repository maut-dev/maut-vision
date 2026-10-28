namespace MautVision.Desktop.Controllers.Projects.Backgrounds.RequestDto;

/// <summary>Нативный диалог выбора файлов; ответ — токен выбора и заголовки файлов относительно размера набора SetId.</summary>
public sealed record SelectBackgroundFilesRequestDto(string ProjectId, Guid SetId);
