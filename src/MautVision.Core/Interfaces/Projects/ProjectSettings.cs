namespace MautVision.Core.Interfaces.Projects;

/// <summary>Размер входа модели в пикселях; обе стороны кратны шагу выбранной базовой модели.</summary>
public sealed record InputSize(int Width, int Height);

/// <summary>
/// Изменяемые пользователем настройки проекта. BaseModelId — базовая модель для обучения;
/// InputSize определяет размер фонов, кадров генерации и датасетов.
/// </summary>
public sealed record ProjectSettings(string Name, string Description, string BaseModelId, InputSize InputSize);
