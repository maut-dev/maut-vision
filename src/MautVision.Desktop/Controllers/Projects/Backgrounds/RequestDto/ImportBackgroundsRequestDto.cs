namespace MautVision.Desktop.Controllers.Projects.Backgrounds.RequestDto;

/// <summary>Рамка для файла выбора: SourceIndex — номер файла в выборе, Left/Top — левый верхний угол в пикселях файла.</summary>
public sealed record BackgroundCropRequestDto(int SourceIndex, int Left, int Top);

/// <summary>Одна рамка на всё видео: левый верхний угол в пикселях кадра.</summary>
public sealed record BackgroundRegionRequestDto(int Left, int Top);

/// <summary>Размытие: радиус от/до 0–1000 px; Seed — 0–4 294 967 295 или null («Из операции»).</summary>
public sealed record BackgroundBlurRequestDto(double MinRadius, double MaxRadius, long? Seed = null);

/// <summary>
/// Запуск подготовки (backgrounds.import) и его примеры (backgrounds.previewImport) — одна форма, чтобы примеры совпали с
/// будущими фонами (тот же OperationId). SelectionToken — из backgrounds.selectFiles или backgrounds.selectVideo; запуск
/// привязывает токен к операции, примеры — нет.
/// Файлы: SourceIndices — номера включённых файлов выбора по порядку (null — все; для «Нарезать из изображений» до 500);
/// Crops — рамки для файлов больше набора (файл без рамки берётся целиком и должен быть ровно размера набора); Count —
/// необязательный предел. Видео: Region, StartMs, IntervalMs (шаг ≥ 1 мс) обязательны; Count — «Сколько фонов нарезать»,
/// от 1 до числа доступных кадров. SkipSimilar — «Пропускать повторяющиеся кадры». Blur — null или радиус до 0 — без размытия.
/// </summary>
public sealed record ImportBackgroundsRequestDto(
    string ProjectId,
    Guid SetId,
    string SelectionToken,
    string OperationId,
    int[]? SourceIndices = null,
    BackgroundCropRequestDto[]? Crops = null,
    BackgroundRegionRequestDto? Region = null,
    long? StartMs = null,
    long? IntervalMs = null,
    int? Count = null,
    bool SkipSimilar = false,
    BackgroundBlurRequestDto? Blur = null);
