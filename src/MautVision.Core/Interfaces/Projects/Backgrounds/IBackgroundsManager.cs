using MautVision.Core.Interfaces.Jobs;

namespace MautVision.Core.Interfaces.Projects.Backgrounds;

/// <summary>
/// Наборы фонов проекта и их фоны. Работают только у открытого проекта, иначе CoreException <c>backgrounds.project_not_open</c>.
/// Изменения сверяют ревизию (<c>backgrounds.revision_conflict</c>) и сообщаются событием
/// <see cref="IProjectsManager.BackgroundsChanged"/>. Фоны добавляет задача <see cref="IImportBackgroundsJob"/>.
/// Отсутствующие набор или фон — <c>backgrounds.set_not_found</c>, <c>backgrounds.item_not_found</c>. Архивный набор только для
/// чтения (<c>backgrounds.set_archived</c>), кроме восстановления и удаления.
/// </summary>
public interface IBackgroundsManager
{
    /// <summary>Все наборы, включая архивные, по времени создания.</summary>
    Task<IReadOnlyList<BackgroundSet>> ListSetsAsync(CancellationToken cancellationToken = default);

    Task<BackgroundSet> GetSetAsync(Guid setId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Набор размера текущего входа проекта. Название 1–120 символов (<c>backgrounds.name_invalid</c>), описание до 4000
    /// (<c>backgrounds.description_invalid</c>).
    /// </summary>
    Task<BackgroundSet> CreateSetAsync(string name, string description, CancellationToken cancellationToken = default);

    Task<BackgroundSet> UpdateSetAsync(Guid setId, string name, string description, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>Набор, в который идёт подготовка, в архив не перенести (<c>backgrounds.set_importing</c>).</summary>
    Task<BackgroundSet> SetSetArchivedAsync(Guid setId, bool archived, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>Страница по порядку добавления; limit 1–200, after — NextCursor предыдущей страницы того же фильтра.</summary>
    Task<BackgroundPage> ListItemsAsync(Guid setId, BackgroundFilter filter, int limit = 100, string? after = null, CancellationToken cancellationToken = default);

    Task<BackgroundItem> GetItemAsync(Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// «Убрать» (removed: true) или «Восстановить» выбранные фоны (до 400) одного набора — всё или ничего, с ревизией каждого
    /// фона; ревизия набора не нужна, поэтому идущий импорт не вызывает конфликтов.
    /// </summary>
    Task<IReadOnlyList<BackgroundItem>> SetItemsRemovedAsync(IReadOnlyList<BackgroundRevision> items, bool removed, CancellationToken cancellationToken = default);

    /// <summary>
    /// Активные фоны набора для генерации по порядку добавления (limit 1–1000) вместе с ревизией состава и размером набора.
    /// </summary>
    Task<BackgroundActivePage> ListActiveItemsAsync(Guid setId, int limit = 500, string? after = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// История подготовки набора: задачи <see cref="IImportBackgroundsJob"/> этого набора, новые первыми (limit 1–200, before —
    /// ID последней задачи предыдущей страницы). Итог — <see cref="ImportBackgroundsResult"/> в <see cref="JobInfo.Result"/>.
    /// </summary>
    Task<IReadOnlyList<JobInfo>> ListRunsAsync(Guid setId, int limit = 50, Guid? before = null, CancellationToken cancellationToken = default);

    Task<BackgroundDeletionReview> ReviewSetDeletionAsync(Guid setId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Удаляет набор, его фоны и историю подготовки; файлы — в фоне. Ошибки: <c>backgrounds.deletion_revision_conflict</c>,
    /// <c>backgrounds.deletion_blocked</c>.
    /// </summary>
    Task<BackgroundDeletionResult> DeleteSetAsync(Guid setId, string reviewRevision, CancellationToken cancellationToken = default);

    /// <summary>Состав удаления выбранных фонов (до 400) одного набора; <c>backgrounds.selection_invalid</c> для пустого выбора.</summary>
    Task<BackgroundDeletionReview> ReviewItemsDeletionAsync(Guid setId, IReadOnlyList<Guid> itemIds, CancellationToken cancellationToken = default);

    Task<BackgroundDeletionResult> DeleteItemsAsync(Guid setId, IReadOnlyList<Guid> itemIds, string reviewRevision, CancellationToken cancellationToken = default);

    /// <summary>
    /// Заголовки выбранных файлов (без полного декодирования): размер с ориентацией и пригодность для набора — до запуска
    /// видно, сколько файлов нужного размера и сколько будет пропущено. Пути абсолютные; порядок ответа — порядок путей.
    /// </summary>
    Task<IReadOnlyList<BackgroundSourceInfo>> InspectFilesAsync(Guid setId, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Кадр файла для рамки: масштабированное декодирование до maxSide (64–4096) по большей стороне. Нечитаемый файл —
    /// <c>backgrounds.image_unreadable</c>.
    /// </summary>
    Task<BackgroundSourcePreview> PreviewFileAsync(string path, int maxSide = 1600, CancellationToken cancellationToken = default);

    /// <summary>
    /// Кадр видео на отметке (первый кадр не раньше неё) для рамки, уменьшенный до maxSide. Отметка за концом видеодорожки —
    /// <c>video.frame_unavailable</c>; ошибки чтения — коды <c>video.*</c>.
    /// </summary>
    Task<BackgroundSourcePreview> PreviewVideoFrameAsync(string path, long timestampMs, int maxSide = 1600, CancellationToken cancellationToken = default);

    /// <summary>
    /// До 6 примеров запуска с тем же входом и operationId: те же рамки, радиусы и пиксели, что даст задача. Примеры
    /// распределены по источникам; у видео — по первым RequestedCount кадрам, переходом к кадрам, и при SkipSimilar похожий
    /// кадр заменяется следующим. Ошибки входа — как у задачи.
    /// </summary>
    Task<BackgroundImportPreview> PreviewImportAsync(ImportBackgroundsInput input, string operationId, CancellationToken cancellationToken = default);
}
