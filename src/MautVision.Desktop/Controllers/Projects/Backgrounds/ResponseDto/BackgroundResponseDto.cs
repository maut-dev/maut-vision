namespace MautVision.Desktop.Controllers.Projects.Backgrounds.ResponseDto;

/// <summary>Вырезанная область исходного кадра в его пикселях.</summary>
public sealed record BackgroundCropAreaDto(int Left, int Top, int Width, int Height);

/// <summary>
/// Происхождение фона. Kind: uploaded («Готовое изображение»), image-crop («Фрагмент изображения»), video-frame («Кадр видео»).
/// SourceName — имя файла или видео; поля кадра (VideoName, TimestampMs, FrameIndex) — только у video-frame. Crop — область
/// («вырезан из x L, y T»), null — кадр целиком. BlurRadius — радиус размытия, 0 — резкий, null — запуск без размытия.
/// </summary>
public sealed record BackgroundOriginResponseDto(
    string Kind,
    string OperationId,
    string SourceName,
    string? VideoName,
    long? TimestampMs,
    long? FrameIndex,
    BackgroundCropAreaDto? Crop,
    double? BlurRadius,
    string ProcessorVersion);

/// <summary>
/// Фон: PNG ровно размера набора. Revision передаётся в «Убрать»/«Восстановить»; RemovedAt — убран. OriginalUrl (PNG) и
/// ThumbnailUrl (JPEG до 256 px) — URL канала ресурсов на ограниченное время.
/// </summary>
public sealed record BackgroundResponseDto(
    Guid Id,
    Guid SetId,
    int Width,
    int Height,
    long ByteSize,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RemovedAt,
    BackgroundOriginResponseDto Origin,
    string OriginalUrl,
    string ThumbnailUrl);

/// <summary>Страница по порядку добавления; NextCursor — значение after для следующей страницы того же фильтра, null на последней.</summary>
public sealed record BackgroundPageResponseDto(BackgroundResponseDto[] Items, string? NextCursor);
