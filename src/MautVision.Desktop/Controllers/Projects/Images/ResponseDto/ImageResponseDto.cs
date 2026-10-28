namespace MautVision.Desktop.Controllers.Projects.Images.ResponseDto;

/// <summary>
/// Происхождение кадра. Kind: file, video, generated. SourceFamilyId объединяет кадры одного источника. Поля видео
/// (VideoName, TimestampMs, FrameIndex) и генерации (GenerationRunId, SampleIndex, RecipeRevision) — null у других видов.
/// </summary>
public sealed record ImageLineageResponseDto(
    string Kind,
    string SourceFamilyId,
    string? VideoName,
    long? TimestampMs,
    long? FrameIndex,
    Guid? GenerationRunId,
    int? SampleIndex,
    long? RecipeRevision);

/// <summary>
/// Изображение группы. Format: png, jpeg, webp, bmp. Width/Height — с учётом EXIF-ориентации (как показывает &lt;img&gt;).
/// Revision растёт при сохранении разметки, архиве и перемещении; AnnotationRevision — текущая ревизия разметки (её
/// передают в images.saveAnnotation). ObjectCount и ReviewState (unreviewed, reviewed) — из текущей разметки.
/// OriginalUrl и ThumbnailUrl (JPEG до 256 px) — URL канала ресурсов на ограниченное время.
/// </summary>
public sealed record ImageResponseDto(
    Guid Id,
    Guid GroupId,
    string DisplayName,
    string Format,
    int Width,
    int Height,
    long ByteSize,
    long Revision,
    long AnnotationRevision,
    int ObjectCount,
    string ReviewState,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ArchivedAt,
    ImageLineageResponseDto Lineage,
    string OriginalUrl,
    string ThumbnailUrl);

/// <summary>Страница по порядку импорта; NextCursor — значение after для следующей страницы того же фильтра, null на последней.</summary>
public sealed record ImagePageResponseDto(ImageResponseDto[] Items, string? NextCursor);

/// <summary>Результат перемещения: обе группы с новыми ревизиями и перемещённые изображения.</summary>
public sealed record MovedImagesResponseDto(ImageGroupResponseDto Source, ImageGroupResponseDto Target, ImageResponseDto[] Images);

/// <summary>
/// Выбор в нативном диалоге. Kind: files или folder. SelectionToken — null, если пользователь закрыл диалог; действует
/// 30 минут и только на этой странице. FileCount — число выбранных файлов (для папки null: состав определит задача).
/// FolderName — имя выбранной папки без пути (для файлов null). Пути в UI не передаются.
/// </summary>
public sealed record ImageSelectionResponseDto(string Kind, string? SelectionToken, int? FileCount, string? FolderName);
