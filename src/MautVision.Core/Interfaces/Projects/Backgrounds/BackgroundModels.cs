using MautVision.Core.Interfaces.Projects.Catalog;

namespace MautVision.Core.Interfaces.Projects.Backgrounds;

/// <summary>
/// Набор фонов. TargetSize — копия размера входа проекта при создании, не меняется; MatchesProjectInput — false, если размер
/// входа проекта позже изменился («Размер набора W × H отличается от входа проекта»). Revision растёт при изменении названия,
/// описания и архиве; ContentRevision — при каждом изменении состава (добавление, «Убрать», «Восстановить», удаление).
/// ItemCount — активные фоны, RemovedCount — убранные.
/// </summary>
public sealed record BackgroundSet(
    Guid Id,
    string Name,
    string Description,
    InputSize TargetSize,
    long Revision,
    long ContentRevision,
    long ItemCount,
    long RemovedCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    bool MatchesProjectInput);

/// <summary>Происхождение фона: готовое изображение, фрагмент изображения или кадр видео.</summary>
public enum BackgroundOriginKind
{
    Uploaded,
    ImageCrop,
    VideoFrame,
}

/// <summary>Вырезанная область исходного кадра в его пикселях (с учётом EXIF-ориентации); размер — размер набора.</summary>
public sealed record BackgroundCropArea(int Left, int Top, int Width, int Height);

/// <summary>
/// Откуда фон. SourceName — имя файла или видео; SourceHash — SHA-256 исходного файла (у кадра — видео); поля кадра видео
/// заполнены только у VideoFrame. Crop — вырезанная область (null — кадр взят целиком). BlurRadius — радиус размытия
/// (0 — фон оставлен резким), null — запуск без размытия. ProcessorVersion — версия обработки кадра.
/// </summary>
public sealed record BackgroundOrigin(
    BackgroundOriginKind Kind,
    string OperationId,
    string SourceName,
    string SourceHash,
    string? VideoName,
    long? TimestampMs,
    long? FrameIndex,
    BackgroundCropArea? Crop,
    double? BlurRadius,
    string ProcessorVersion);

/// <summary>
/// Фон — PNG RGBA ровно размера набора, единственный файл files/backgrounds/&lt;id&gt;.png. PixelHash — SHA-256 пикселей
/// (точные повторы в наборе). Revision растёт при «Убрать» и «Восстановить»; RemovedAt — убран (обратимо).
/// Порядок в наборе — порядок добавления.
/// </summary>
public sealed record BackgroundItem(
    Guid Id,
    Guid SetId,
    int Width,
    int Height,
    long ByteSize,
    string Sha256,
    string PixelHash,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RemovedAt,
    BackgroundOrigin Origin)
{
    /// <summary>Вид материала в канале ресурсов: <c>new ResourceReference(ResourceKind, item.Id)</c>.</summary>
    public const string ResourceKind = "backgrounds";
}

/// <summary>Removed: null — все, true — только убранные, false — только активные.</summary>
public sealed record BackgroundFilter(bool? Removed = false);

/// <summary>Страница фонов набора по порядку добавления; NextCursor — null на последней странице.</summary>
public sealed record BackgroundPage(IReadOnlyList<BackgroundItem> Items, string? NextCursor);

/// <summary>
/// Страница активных фонов набора для генерации: ContentRevision и TargetSize прочитаны вместе со страницей. Если ревизия
/// состава между страницами изменилась, состав набора менялся во время чтения.
/// </summary>
public sealed record BackgroundActivePage(Guid SetId, InputSize TargetSize, long ContentRevision, IReadOnlyList<BackgroundItem> Items, string? NextCursor);

/// <summary>Фон с ревизией, которую видел пользователь.</summary>
public sealed record BackgroundRevision(Guid Id, long Revision);

/// <summary>
/// Состав удаления навсегда: весь набор (WholeSet) или выбранные фоны. Revision передаётся в удаление; непустой Blockers —
/// удаление сейчас недоступно (<c>backgrounds.import_active</c>, пресеты генерации).
/// </summary>
public sealed record BackgroundDeletionReview(Guid SetId, string SetName, bool WholeSet, long Items, IReadOnlyList<DeletionBlocker> Blockers, string Revision);

/// <summary>Строки удалены; CleanupPending — файлы ещё удаляются в фоне.</summary>
public sealed record BackgroundDeletionResult(Guid SetId, bool SetDeleted, long Items, bool CleanupPending);

/// <summary>Как файл подходит набору: ровно нужного размера, больше (можно вырезать рамкой), меньше или нечитаемый.</summary>
public enum BackgroundSourceFit
{
    Exact,
    Larger,
    Smaller,
    Unreadable,
}

/// <summary>Заголовок выбранного файла: имя без пути, размер с учётом EXIF-ориентации (null у нечитаемого) и пригодность.</summary>
public sealed record BackgroundSourceInfo(string Name, int? Width, int? Height, BackgroundSourceFit Fit);

/// <summary>Закодированное изображение для показа (PNG или JPEG); не хранится в проекте.</summary>
public sealed record BackgroundPreviewImage(byte[] Content, string ContentType, int Width, int Height);

/// <summary>Кадр для рамки: размер исходного кадра (с ориентацией) и уменьшенная копия не больше заданной стороны.</summary>
public sealed record BackgroundSourcePreview(int Width, int Height, BackgroundPreviewImage Preview);

/// <summary>
/// Пример будущего фона. SourceIndex — номер источника запуска (у видео — номер кадра диапазона), TimestampMs — отметка кадра.
/// Reason — код причины пропуска (тогда Image null): <c>backgrounds.wrong_size</c>, <c>backgrounds.crop_out_of_bounds</c>
/// (с ActualWidth/ActualHeight), <c>backgrounds.image_unreadable</c>, <c>backgrounds.similar</c>, <c>backgrounds.duplicate</c>,
/// <c>video.frame_unavailable</c>. Image — PNG ровно размера набора, те же пиксели, что даст запуск с этой операцией.
/// </summary>
public sealed record BackgroundImportSample(
    int SourceIndex,
    string SourceName,
    long? TimestampMs,
    string? Reason,
    int? ActualWidth,
    int? ActualHeight,
    BackgroundCropArea? Crop,
    double? BlurRadius,
    BackgroundPreviewImage? Image);

/// <summary>Примеры запуска: размер набора, размытие с выбранным seed (null — без размытия) и до 6 примеров.</summary>
public sealed record BackgroundImportPreview(InputSize TargetSize, BackgroundBlur? Blur, IReadOnlyList<BackgroundImportSample> Samples);
