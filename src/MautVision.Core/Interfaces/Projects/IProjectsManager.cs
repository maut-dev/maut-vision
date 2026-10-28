using MautVision.Core.Interfaces.Projects.Backgrounds;
using MautVision.Core.Interfaces.Projects.Generation;
using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Core.Interfaces.Projects.Datasets;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Interfaces.Projects.Training;
using MautVision.Core.Interfaces.Projects.Checks;
using MautVision.Core.Interfaces.Projects.Transfer;

namespace MautVision.Core.Interfaces.Projects;

/// <summary>Provides access to projects without exposing their implementation.</summary>
public interface IProjectsManager
{
    event EventHandler<ProjectOpenedEventArgs>? ProjectOpened;

    /// <summary>Проект создан, импортирован, изменён или перенесён в архив; может прийти из фонового потока.</summary>
    event EventHandler<ProjectChangedEventArgs>? ProjectChanged;

    /// <summary>Проект удалён: его больше нет в списке, экземпляр недействителен; может прийти из фонового потока.</summary>
    event EventHandler<ProjectRemovedEventArgs>? ProjectRemoved;

    /// <summary>Изменился снимок импорта (ход, итог); прореживается, может прийти из фонового потока.</summary>
    event EventHandler<ProjectImportChangedEventArgs>? ImportChanged;

    /// <summary>Изменился каталог (варианты, классы, привязки, изображения) открытого проекта; может прийти из фонового потока.</summary>
    event EventHandler<CatalogChangedEventArgs>? CatalogChanged;

    /// <summary>Изменились группы изображений, изображения или разметка открытого проекта; может прийти из фонового потока.</summary>
    event EventHandler<ImagesChangedEventArgs>? ImagesChanged;

    /// <summary>Изменились наборы фонов или фоны открытого проекта; может прийти из фонового потока.</summary>
    event EventHandler<BackgroundsChangedEventArgs>? BackgroundsChanged;

    /// <summary>Изменились пресеты генерации или данные запусков открытого проекта; может прийти из фонового потока.</summary>
    event EventHandler<GenerationChangedEventArgs>? GenerationChanged;

    /// <summary>Изменились версии датасетов открытого проекта (появилась, изменена, удалена); может прийти из фонового потока.</summary>
    event EventHandler<DatasetsChangedEventArgs>? DatasetsChanged;

    /// <summary>Изменились запуски обучения (ход, эпохи, журнал, итог) или версии моделей открытого проекта; может прийти из фонового потока.</summary>
    event EventHandler<TrainingChangedEventArgs>? TrainingChanged;

    /// <summary>Изменились оценки на датасетах или живая проверка открытого проекта; может прийти из фонового потока.</summary>
    event EventHandler<ChecksChangedEventArgs>? ChecksChanged;

    /// <summary>Все проекты, включая архивные. Проект с повреждёнными метаданными пропускается.</summary>
    Task<IReadOnlyList<IProject>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Проект по ID; отсутствующий — CoreException <c>projects.not_found</c>.</summary>
    Task<IProject> GetAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Готовит проект к работе: создаёт или обновляет его хранилище. Хранилище новее приложения —
    /// CoreException <c>projects.storage_newer</c>, неудачное обновление — <c>projects.storage_migration_failed</c>.
    /// Открытие — переход в проект: последний из начатых вызовов запоминается как последний проект и останавливает живые проверки
    /// других проектов; более раннее медленное открытие, завершившееся позже, этого не делает (его проект просто готов к работе).
    /// </summary>
    Task<IProject> OpenAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>Создаёт проект; выбранная базовая модель должна быть установлена.</summary>
    Task<IProject> CreateAsync(ProjectSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Последний открытый проект (запоминается при OpenAsync, забывается при удалении); null, если его нет.</summary>
    Task<string?> GetLastProjectIdAsync(CancellationToken cancellationToken = default);

    /// <summary>Что удаление проекта отменит: идущие задачи и живая проверка; TransferActive — идёт экспорт.</summary>
    Task<ProjectDeletionReview> ReviewDeletionAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Удаляет проект с компьютера: отменяет задачи и живую проверку (только при cancelJobs, иначе при их наличии —
    /// <c>projects.busy</c>) и ждёт их остановки, закрывает хранилище, ставит отметку удаления (project.json → project.deleted),
    /// переименовывает папку в projects/.deleted-&lt;id&gt;-&lt;guid&gt; и удаляет её в фоне. Ревизия изменилась —
    /// <c>projects.deletion_conflict</c>; идёт экспорт проекта — <c>projects.transfer_active</c>. Занятые файлы удаляются при
    /// следующем запуске (<see cref="CleanUpLeftoversAsync"/>), результат тогда CleanupPending.
    /// </summary>
    Task<ProjectDeletionResult> DeleteAsync(string projectId, long expectedRevision, bool cancelJobs, CancellationToken cancellationToken = default);

    /// <summary>
    /// Готовит экспорт проекта в destinationPath (.zip в существующей папке; иначе <c>projects.export_destination</c>): готовит
    /// хранилище проекта, не делая его последним открытым, и проверяет, что в проекте нет активных задач и живой проверки
    /// (<c>projects.transfer_busy</c>). Задачу ставит потребитель: <c>IJobsManager.EnqueueAsync&lt;IProjectExportJob, ProjectExportInput&gt;</c>
    /// с тем же operationId; повтор с operationId уже поставленной задачи возвращает её входные данные без проверок.
    /// </summary>
    Task<ProjectExportInput> PrepareExportAsync(string projectId, string destinationPath, string operationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Начинает импорт архива проекта и сразу возвращает снимок; ход и итог приходят событием <see cref="ImportChanged"/>. Повтор с
    /// тем же operationId возвращает тот же импорт. Одновременно идёт один импорт (<c>projects.import_active</c>). Отказы приходят
    /// в снимке: <c>projects.archive_changed</c>, <c>projects.archive_damaged</c>, <c>projects.archive_unknown</c>,
    /// <c>projects.archive_legacy</c> (архив неподдерживаемого раннего формата), <c>projects.disk_full</c>, <c>projects.storage_newer</c>.
    /// Отмена и ошибка не оставляют изменений.
    /// </summary>
    Task<ProjectImportInfo> StartImportAsync(ProjectArchiveSource source, string operationId, CancellationToken cancellationToken = default);

    /// <summary>Импорт по ID; неизвестный — <c>projects.import_not_found</c>.</summary>
    ProjectImportInfo GetImport(Guid importId);

    /// <summary>Импорты этого запуска приложения, новые первыми.</summary>
    IReadOnlyList<ProjectImportInfo> ListImports();

    /// <summary>Идемпотентная отмена импорта: снимок с CancelRequested сразу, Cancelled — событием после отката.</summary>
    ProjectImportInfo CancelImport(Guid importId);

    /// <summary>
    /// Фоновая очистка остатков прошлых запусков: папок удалённых проектов (.deleted-*, отметки project.deleted) и незавершённых
    /// импортов (.import-*). Смотрит только имена в projects/; вызывается после показа UI, а не при инициализации.
    /// </summary>
    Task CleanUpLeftoversAsync(CancellationToken cancellationToken = default);
}
