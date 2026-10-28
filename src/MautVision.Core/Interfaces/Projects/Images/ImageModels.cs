using MautVision.Core.Interfaces.Projects.Catalog;

namespace MautVision.Core.Interfaces.Projects.Images;

/// <summary>Происхождение группы задаётся при создании: ручные группы наполняет импорт, сгенерированные — генерация.</summary>
public enum ImageGroupOrigin
{
    Manual,
    Generated,
}

/// <summary>Назначение группы при сборке датасета: «Без назначения / Обучение / Проверка / Тест».</summary>
public enum ImageSplitHint
{
    Unassigned,
    Train,
    Validation,
    Test,
}

/// <summary>Состояние наполнения сгенерированной группы; у ручных групп — null.</summary>
public enum ImageGenerationState
{
    Producing,
    Completed,
    Partial,
}

/// <summary>Reviewed — разметка проверена, в том числе осознанно пустой кадр.</summary>
public enum ImageReviewState
{
    Unreviewed,
    Reviewed,
}

/// <summary>Формат исходного файла; определяется по содержимому.</summary>
public enum ImageFileFormat
{
    Png,
    Jpeg,
    Webp,
    Bmp,
}

public enum ImageLineageKind
{
    File,
    Video,
    Generated,
}

/// <summary>
/// Группа изображений. Название 1–120 символов (повторы допустимы), описание до 4000. ImageCount — все изображения группы,
/// включая архивные; ArchivedImageCount — архивные. Revision растёт при изменении полей группы, архиве и перемещении
/// изображений; импорт её не меняет (меняются только счётчики).
/// </summary>
public sealed record ImageGroup(
    Guid Id,
    string Name,
    string Description,
    ImageGroupOrigin Origin,
    ImageSplitHint SplitHint,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    Guid? GenerationRunId,
    ImageGenerationState? GenerationState,
    long ImageCount,
    long ArchivedImageCount);

/// <summary>
/// Происхождение кадра. SourceFamilyId объединяет кадры одного источника, чтобы датасет не разносил их по разным split:
/// <c>file:&lt;pixelHash&gt;</c>, <c>video:&lt;хеш видео&gt;</c>. Поля видео и генерации заполнены только у своего вида.
/// </summary>
public sealed record ImageLineage(
    ImageLineageKind Kind,
    string SourceFamilyId,
    string? VideoName = null,
    long? TimestampMs = null,
    long? FrameIndex = null,
    Guid? GenerationRunId = null,
    int? SampleIndex = null,
    long? RecipeRevision = null);

/// <summary>
/// Изображение группы. Файл хранится без изменений в files/images/&lt;id&gt;.&lt;ext&gt;. Width/Height — с учётом
/// EXIF-ориентации; координаты разметки относятся к ориентированному кадру. Sha256 — хеш файла, PixelHash — хеш
/// ориентированных пикселей RGBA (повторы в группе). Revision растёт при сохранении разметки, архиве и перемещении;
/// AnnotationRevision — номер текущей ревизии разметки. ObjectCount и ReviewState — из текущей разметки.
/// </summary>
public sealed record ProjectImage(
    Guid Id,
    Guid GroupId,
    string DisplayName,
    ImageFileFormat Format,
    int Width,
    int Height,
    long ByteSize,
    string Sha256,
    string PixelHash,
    long Revision,
    long AnnotationRevision,
    int ObjectCount,
    ImageReviewState ReviewState,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ArchivedAt,
    string OperationId,
    ImageLineage Lineage)
{
    /// <summary>Вид материала в канале ресурсов: <c>new ResourceReference(ResourceKind, image.Id)</c>.</summary>
    public const string ResourceKind = "images";
}

/// <summary>Рамка в долях кадра 0–1 от левого верхнего угла ориентированного изображения.</summary>
public sealed record ImageBox(double X, double Y, double Width, double Height);

/// <summary>Объект разметки. ClassVariantId — вариант класса (привязка) этого класса или null («Без варианта»).</summary>
public sealed record AnnotationObject(Guid Id, Guid ClassId, Guid? ClassVariantId, ImageBox Box);

/// <summary>Неизменяемая ревизия разметки изображения. Roi — одна рамка области или null.</summary>
public sealed record ImageAnnotation(
    Guid ImageId,
    long Revision,
    IReadOnlyList<AnnotationObject> Objects,
    ImageBox? Roi,
    ImageReviewState ReviewState,
    DateTimeOffset CreatedAt);

/// <summary>Объект для сохранения: Id — существующий объект текущей ревизии или null (ID назначит Core).</summary>
public sealed record AnnotationObjectInput(Guid? Id, Guid ClassId, Guid? ClassVariantId, ImageBox Box);

public sealed record AnnotationInput(IReadOnlyList<AnnotationObjectInput> Objects, ImageBox? Roi, ImageReviewState ReviewState);

/// <summary>Результат сохранения разметки: изображение с новыми ревизиями и новая ревизия разметки.</summary>
public sealed record SavedAnnotation(ProjectImage Image, ImageAnnotation Annotation);

/// <summary>Фильтр страницы: Archived — null все, true только архивные, false только активные; ReviewState — null любые.</summary>
public sealed record ImageFilter(bool? Archived = false, ImageReviewState? ReviewState = null);

/// <summary>Страница изображений группы по порядку импорта; NextCursor — null на последней странице.</summary>
public sealed record ImagePage(IReadOnlyList<ProjectImage> Items, string? NextCursor);

/// <summary>Изображение с ревизией, которую видел пользователь.</summary>
public sealed record ImageRevision(Guid Id, long Revision);

/// <summary>
/// Состав удаления навсегда: вся группа (WholeGroup) или выбранные изображения. Revision передаётся в удаление; непустой
/// Blockers — удаление сейчас недоступно (<c>images.import_active</c>, позже — генерация).
/// </summary>
public sealed record ImageDeletionReview(
    Guid GroupId,
    string GroupName,
    bool WholeGroup,
    long Images,
    IReadOnlyList<DeletionBlocker> Blockers,
    string Revision);

/// <summary>Строки удалены; CleanupPending — файлы ещё удаляются в фоне.</summary>
public sealed record ImageDeletionResult(Guid GroupId, bool GroupDeleted, long Images, bool CleanupPending);

/// <summary>Результат перемещения: обе группы с новыми ревизиями и перемещённые изображения.</summary>
public sealed record MovedImages(ImageGroup Source, ImageGroup Target, IReadOnlyList<ProjectImage> Images);
