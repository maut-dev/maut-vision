using MautVision.Core.Interfaces.Jobs;

namespace MautVision.Core.Interfaces.Projects.Images;

/// <summary>
/// Группы изображений проекта, изображения и их разметка. Работают только у открытого проекта, иначе CoreException
/// <c>images.project_not_open</c>. Изменения сверяют ревизию (<c>images.revision_conflict</c>) и сообщаются событием
/// <see cref="IProjectsManager.ImagesChanged"/>. Изображения добавляет задача <see cref="IImportImagesJob"/>.
/// Отсутствующие группа или изображение — <c>images.group_not_found</c>, <c>images.image_not_found</c>.
/// Архивная группа только для чтения (<c>images.group_archived</c>), кроме восстановления и удаления; группа, которую
/// наполняет генерация, — <c>images.group_busy</c>.
/// </summary>
public interface IImagesManager
{
    /// <summary>Все группы проекта, включая архивные, по времени создания.</summary>
    Task<IReadOnlyList<ImageGroup>> ListGroupsAsync(CancellationToken cancellationToken = default);

    Task<ImageGroup> GetGroupAsync(Guid groupId, CancellationToken cancellationToken = default);

    /// <summary>Ручная группа. Ошибки: <c>images.name_invalid</c>, <c>images.description_invalid</c>.</summary>
    Task<ImageGroup> CreateGroupAsync(string name, string description, ImageSplitHint splitHint, CancellationToken cancellationToken = default);

    Task<ImageGroup> UpdateGroupAsync(Guid groupId, string name, string description, ImageSplitHint splitHint, long expectedRevision,
        CancellationToken cancellationToken = default);

    /// <summary>Группу, в которую идёт импорт, в архив не перенести (<c>images.group_importing</c>).</summary>
    Task<ImageGroup> SetGroupArchivedAsync(Guid groupId, bool archived, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>Страница по порядку импорта; limit 1–200, after — NextCursor предыдущей страницы того же фильтра.</summary>
    Task<ImagePage> ListImagesAsync(Guid groupId, ImageFilter filter, int limit = 100, string? after = null, CancellationToken cancellationToken = default);

    Task<ProjectImage> GetImageAsync(Guid imageId, CancellationToken cancellationToken = default);

    /// <summary>Текущая ревизия разметки или указанная (история неизменяема).</summary>
    Task<ImageAnnotation> GetAnnotationAsync(Guid imageId, long? revision = null, CancellationToken cancellationToken = default);

    /// <summary>Текущая разметка до 200 изображений одним запросом (рамки на миниатюрах страницы); отсутствующие пропускаются.</summary>
    Task<IReadOnlyList<ImageAnnotation>> ListAnnotationsAsync(IReadOnlyList<Guid> imageIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Сохраняет новую ревизию разметки. expectedAnnotationRevision — AnnotationRevision, которую видел пользователь.
    /// Рамки 0–1 (<c>images.box_invalid</c>); Id объекта — из текущей ревизии (<c>images.object_id_invalid</c>);
    /// новые и изменённые по классу или варианту объекты ссылаются на активные класс и вариант класса
    /// (<c>images.reference_unavailable</c>), неизменённые могут сохранить архивную ссылку. Архивное изображение —
    /// <c>images.image_archived</c>.
    /// </summary>
    Task<SavedAnnotation> SaveAnnotationAsync(Guid imageId, AnnotationInput annotation, long expectedAnnotationRevision,
        CancellationToken cancellationToken = default);

    /// <summary>Архив и восстановление выбранных изображений (до 400) — всё или ничего, с ревизией каждого.</summary>
    Task<IReadOnlyList<ProjectImage>> SetImagesArchivedAsync(IReadOnlyList<ImageRevision> images, bool archived,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Перемещает активные изображения (до 400) в другую группу того же происхождения (<c>images.origin_mismatch</c>) —
    /// всё или ничего, с ревизиями обеих групп и каждого изображения. Изображения встают в конец порядка цели.
    /// </summary>
    Task<MovedImages> MoveImagesAsync(Guid sourceGroupId, long sourceRevision, Guid targetGroupId, long targetRevision,
        IReadOnlyList<ImageRevision> images, CancellationToken cancellationToken = default);

    /// <summary>
    /// История импорта группы: задачи <see cref="IImportImagesJob"/> этой группы, новые первыми (limit 1–200, before — ID
    /// последней задачи предыдущей страницы). Итог с ошибками по файлам — в <see cref="JobInfo.Result"/>.
    /// </summary>
    Task<IReadOnlyList<JobInfo>> ListImportsAsync(Guid groupId, int limit = 50, Guid? before = null, CancellationToken cancellationToken = default);

    /// <summary>Состав удаления всей группы.</summary>
    Task<ImageDeletionReview> ReviewGroupDeletionAsync(Guid groupId, CancellationToken cancellationToken = default);

    /// <summary>Удаляет группу, её изображения, разметку и историю импорта. Ошибки: <c>images.deletion_revision_conflict</c>, <c>images.deletion_blocked</c>.</summary>
    Task<ImageDeletionResult> DeleteGroupAsync(Guid groupId, string reviewRevision, CancellationToken cancellationToken = default);

    /// <summary>Состав удаления выбранных изображений (до 400) одной группы.</summary>
    Task<ImageDeletionReview> ReviewImagesDeletionAsync(Guid groupId, IReadOnlyList<Guid> imageIds, CancellationToken cancellationToken = default);

    Task<ImageDeletionResult> DeleteImagesAsync(Guid groupId, IReadOnlyList<Guid> imageIds, string reviewRevision,
        CancellationToken cancellationToken = default);
}
