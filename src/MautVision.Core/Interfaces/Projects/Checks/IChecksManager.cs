namespace MautVision.Core.Interfaces.Projects.Checks;

/// <summary>
/// Проверки версий моделей проекта: оценка на части датасета (задача <see cref="IModelEvaluationJob"/>, хранится в project.db) и
/// живые проверки по изображению, видео и экрану (в памяти, одна на приложение: занимает видеокарту). Работает только у открытого
/// проекта (<c>checks.project_not_open</c>). Изменения — <see cref="IProjectsManager.ChecksChanged"/>, состояние задач оценки —
/// JobChanged. Версии и датасеты читаются в одну сторону; отсутствующая версия — <c>training.version_not_found</c>.
/// </summary>
public interface IChecksManager
{
    /// <summary>
    /// Проверяет оценку и готовит вход задачи (её ставит ICore.Jobs.EnqueueAsync с тем же operationId). Проверки: среда worker
    /// (<c>checks.runtime_not_configured</c>, <c>checks.runtime_missing</c>), параметры (<c>checks.evaluation_parameters</c> с
    /// Details["field"]), датасет готов (datasets.unavailable, archived, corrupt), классы и их порядок совпадают с версией
    /// (<c>checks.evaluation_classes_mismatch</c>), размер кадров равен входу версии (<c>checks.evaluation_input_mismatch</c>, Details:
    /// datasetWidth, datasetHeight, versionWidth, versionHeight), в части есть кадры (<c>checks.evaluation_split_empty</c>, Details:
    /// split — Validation | Test). Повтор той же операции возвращает её вход.
    /// </summary>
    Task<ModelEvaluationInput> PrepareEvaluationAsync(ModelEvaluationRequest request, string operationId, CancellationToken cancellationToken = default);

    /// <summary>Оценка по ID; отсутствующая — <c>checks.evaluation_not_found</c>.</summary>
    Task<ModelEvaluation> GetEvaluationAsync(Guid evaluationId, CancellationToken cancellationToken = default);

    /// <summary>«Все проверки», новые первыми (limit 1–200, before — ID последней оценки предыдущей страницы).</summary>
    Task<ModelEvaluationPage> ListEvaluationsAsync(ModelEvaluationFilter filter, int limit = 50, Guid? before = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Матрица сравнения: последняя успешная оценка каждой версии на каждой паре «датасет · часть» (null — любые датасет и часть).
    /// </summary>
    Task<IReadOnlyList<ModelEvaluation>> ListLatestEvaluationsAsync(Guid? datasetVersionId = null, EvaluationSplit? split = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Запускает проверку по изображению и сразу возвращает её снимок (Preparing). Общие отказы живых проверок: среда worker
    /// (<c>checks.runtime_*</c>), параметры (<c>checks.prediction_parameters</c> с Details["field"]), файл не выбран, не того вида
    /// или изменён после выбора (<c>checks.prediction_source</c>, <c>checks.source_changed</c>), видеокарта занята —
    /// <c>checks.gpu_busy_training</c>, <c>checks.gpu_busy_evaluation</c>, <c>checks.gpu_busy_prediction</c>.
    /// </summary>
    Task<LiveCheck> StartImageCheckAsync(ImageCheckRequest request, CancellationToken cancellationToken = default);

    /// <summary>Проверка по видео; рамка вне кадра или не размера входа — <c>checks.region_invalid</c>.</summary>
    Task<LiveCheck> StartVideoCheckAsync(VideoCheckRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Проверка с экрана до остановки или предела времени; нет монитора — <c>screen.monitor_not_found</c>, рамка вне монитора или не
    /// размера входа — <c>checks.region_invalid</c>.
    /// </summary>
    Task<LiveCheck> StartScreenCheckAsync(ScreenCheckRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// «Остановить»: идемпотентно; снимок в Cancelling, итог — Cancelled после остановки worker. Reason — кто остановил: кнопка (User)
    /// или host, когда страница UI перезагружена или окно закрыто (SessionEnded).
    /// </summary>
    Task<LiveCheck> StopCheckAsync(Guid checkId, LiveCheckStopReason reason = LiveCheckStopReason.User, CancellationToken cancellationToken = default);

    /// <summary>
    /// Снимок проверки с последней страницей журнала (journalLimit 0–200 записей); живые проверки не сохраняются между запусками
    /// приложения — неизвестная <c>checks.check_not_found</c>.
    /// </summary>
    Task<LiveCheck> GetCheckAsync(Guid checkId, int journalLimit = 40, CancellationToken cancellationToken = default);

    /// <summary>Живые проверки проекта в памяти, новые первыми: идущая и последние завершённые.</summary>
    Task<IReadOnlyList<LiveCheck>> ListChecksAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Журнал кадров с обнаружениями по возрастанию: after — записи после Ordinal (дочитать новые), before — записи перед ним (листать
    /// назад); без курсоров — последние limit записей (1–200).
    /// </summary>
    Task<LiveCheckJournalPage> ListJournalAsync(Guid checkId, long? after = null, long? before = null, int limit = 100,
        CancellationToken cancellationToken = default);

    /// <summary>Последнее превью проверки (JPEG); null, пока его нет.</summary>
    LiveCheckPreview? GetPreview(Guid checkId);
}
