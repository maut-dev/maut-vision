namespace MautVision.Core.Interfaces.Projects.Training;

/// <summary>
/// Обучение проекта: запуски (задачи <see cref="ITrainingRunJob"/>), их ход, история эпох и журнал, версии моделей. Работает только
/// у открытого проекта (<c>training.project_not_open</c>). Отсутствующие запуск и версия — <c>training.run_not_found</c>,
/// <c>training.version_not_found</c>. Изменения сообщает <see cref="IProjectsManager.TrainingChanged"/>, состояние задачи —
/// JobChanged.
/// </summary>
public interface ITrainingManager
{
    /// <summary>Запуски, новые первыми (limit 1–200, before — ID последнего запуска предыдущей страницы).</summary>
    Task<TrainingRunPage> ListRunsAsync(int limit = 50, Guid? before = null, CancellationToken cancellationToken = default);

    Task<TrainingRun> GetRunAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Проверяет запуск и готовит вход задачи (её ставит ICore.Jobs.EnqueueAsync с тем же operationId). Проверки: среда worker
    /// (<c>training.runtime_not_configured</c>, <c>training.runtime_missing</c>), параметры (<c>training.parameter_invalid</c> с
    /// Details["field"]), размер входа кратен шагу модели (<c>training.input_size_invalid</c>), базовая модель установлена
    /// (<c>training.base_model_not_installed</c>), датасет готов к обучению (коды datasets.*: unavailable, archived, corrupt,
    /// input_size_mismatch, classes_invalid), индексы classes есть в датасете (<c>training.parameter_invalid</c>, field classes).
    /// Повтор той же операции возвращает её вход.
    /// </summary>
    Task<TrainingRunInput> PrepareRunAsync(TrainingRunRequest request, string operationId, CancellationToken cancellationToken = default);

    /// <summary>История эпох запуска по возрастанию (limit 1–1000, after — номер последней полученной эпохи).</summary>
    Task<TrainingEpochPage> ListEpochsAsync(Guid runId, int? after = null, int limit = 500, CancellationToken cancellationToken = default);

    /// <summary>Журнал запуска по возрастанию (limit 1–1000, after — Sequence последней полученной записи).</summary>
    Task<TrainingLogPage> ListLogAsync(Guid runId, long? after = null, int limit = 200, CancellationToken cancellationToken = default);

    /// <summary>Последние записи журнала («последние сообщения»), по возрастанию; NextAfter — для продолжения с конца.</summary>
    Task<TrainingLogPage> TailLogAsync(Guid runId, int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>Версии моделей, новые первыми.</summary>
    Task<IReadOnlyList<ModelVersion>> ListVersionsAsync(CancellationToken cancellationToken = default);

    Task<ModelVersion> GetVersionAsync(Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>«Сделать рабочей»: ровно одна рабочая версия; повтор ничего не меняет.</summary>
    Task<ModelVersion> SetWorkingVersionAsync(Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Состав удаления версии для общего диалога: оценки на датасетах, которые уйдут вместе с ней, размер файлов и причины, по
    /// которым удалить сейчас нельзя (<see cref="ModelVersionDeletionReview.Blockers"/>).
    /// </summary>
    Task<ModelVersionDeletionReview> ReviewVersionDeletionAsync(Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Удаляет версию: строка, файлы версии в списке и её оценки на датасетах — одной транзакцией, папка файлов — фоном
    /// (FileCleanup). Рабочую версию и версию, которую сейчас проверяют или выгружают, удалить нельзя:
    /// <c>training.version_in_use</c> с Details["reason"] — working | evaluation | check | export. Событие TrainingChanged
    /// сообщает версию с Revision = null.
    /// </summary>
    Task<ModelVersionDeletionResult> DeleteVersionAsync(Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Вход задачи <see cref="IModelExportJob"/>: версия существует, путь ZIP — абсолютный, выбранный в «Сохранить как». Повтор той
    /// же операции возвращает её вход.
    /// </summary>
    Task<ModelExportInput> PrepareExportAsync(Guid versionId, string destinationPath, string operationId, CancellationToken cancellationToken = default);
}
