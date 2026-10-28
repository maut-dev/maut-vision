namespace MautVision.Core.Interfaces.Jobs;

/// <summary>
/// Задачи проекта хранятся в его project.db и выполняются, только пока проект открыт
/// (<see cref="Projects.IProjectsManager.OpenAsync"/>). Операции с задачами проекта, который в этом
/// процессе не открывали, отклоняются кодом <c>jobs.project_not_open</c>. Глобальных задач нет.
/// </summary>
public interface IJobsManager
{
    /// <summary>Снимок после каждого сохранённого изменения, в порядке Sequence; может прийти из фонового потока.</summary>
    event EventHandler<JobChangedEventArgs>? JobChanged;

    /// <summary>
    /// Сохраняет задачу и сразу возвращает её снимок, не дожидаясь выполнения. Повтор с тем же
    /// operationId в проекте возвращает существующую задачу. Ошибка выполнения сохраняется в задаче.
    /// label — подпись для списков задач (имя датасета, номер версии); пробелы по краям убираются, длиннее
    /// <see cref="JobInfo.MaxLabelLength"/> символов обрезается, пустая — null.
    /// </summary>
    Task<JobInfo> EnqueueAsync<TJob, TInput>(TInput input, string projectId, string operationId, string? label = null,
        CancellationToken cancellationToken = default) where TJob : IJobDefinition<TInput>;

    /// <summary>Задача по ID; отсутствующая — CoreException <c>jobs.not_found</c>.</summary>
    Task<JobInfo> GetAsync(string projectId, Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>Задачи проекта, новые первыми, не больше limit (1–200); before — ID последней задачи предыдущей страницы.</summary>
    Task<IReadOnlyList<JobInfo>> ListAsync(string projectId, int limit = 50, Guid? before = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Активные задачи проекта (в очереди и выполняемые, включая отменяемые) в порядке очереди, не больше limit (1–200); after —
    /// NextCursor предыдущей страницы. Total и CountsByKind — точные числа всех активных задач, не только страницы.
    /// </summary>
    Task<ActiveJobsPage> ListActiveAsync(string projectId, int limit = 30, Guid? after = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Идемпотентная отмена. Задача в очереди или Blocked отменяется сразу; выполняемая получает CancelRequested
    /// и становится Cancelled, когда обработчик остановится. Завершённая задача не меняется.
    /// </summary>
    Task<JobInfo> CancelAsync(string projectId, Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>Активные задачи (в очереди и выполняемые) всех открытых в этом процессе проектов, из памяти; например, перед выходом.</summary>
    int CountActive();
}
