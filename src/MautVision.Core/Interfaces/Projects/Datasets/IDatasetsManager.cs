namespace MautVision.Core.Interfaces.Projects.Datasets;

/// <summary>
/// Датасеты проекта: неизменяемые версии готовых кадров и разметки в формате YOLO, планы сборки и операции (сборка, проверка
/// целостности, экспорт ZIP — задачи Jobs). Работает только у открытого проекта (<c>datasets.project_not_open</c>).
/// Отсутствующие или удалённые версия, план, операция, образец — <c>datasets.unavailable</c>; изменения сверяют ревизию
/// (<c>datasets.revision_conflict</c>) и сообщаются событием <see cref="IProjectsManager.DatasetsChanged"/>. Архивная
/// версия только для чтения: её нельзя изменить, проверить, экспортировать и передать обучению (<c>datasets.archived</c>).
/// </summary>
public interface IDatasetsManager
{
    /// <summary>Готовые версии, новые первыми; includeArchived: false — без архивных.</summary>
    Task<IReadOnlyList<DatasetVersion>> ListAsync(bool includeArchived = true, CancellationToken cancellationToken = default);

    /// <summary>Число готовых версий для обзора проекта: рабочие (не в архиве и не повреждённые), архивные и повреждённые вне архива.</summary>
    Task<DatasetsSummary> GetSummaryAsync(CancellationToken cancellationToken = default);

    Task<DatasetVersion> GetAsync(Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>Паспорт версии для моделей: спецификация, классы, состав и hash manifest — и после удаления версии.</summary>
    Task<DatasetVersion> GetPassportAsync(Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// «Изменить»: название 1–120 (<c>datasets.name_invalid</c>), описание до 4000 (<c>datasets.description_invalid</c>).
    /// </summary>
    Task<DatasetVersion> UpdateAsync(Guid versionId, string name, string description, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>«В архив» / «Восстановить»; повтор с тем же значением ничего не меняет.</summary>
    Task<DatasetVersion> SetArchivedAsync(Guid versionId, bool archived, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>
    /// «Проверить перед сборкой»: проверяет спецификацию (<c>datasets.name_invalid</c>, <c>description_invalid</c>,
    /// <c>selection_invalid</c>, <c>variants_invalid</c>, <c>split_invalid</c>), группы (<c>datasets.group_unavailable</c>) и
    /// классы (<c>datasets.class_unavailable</c>), отбирает кадры, назначает части и считает состав; роль «Отказ» —
    /// <c>datasets.unselected_class</c>, <c>datasets.unassigned_variant</c>; вариант выбранного класса без роли —
    /// <c>datasets.variant_role_missing</c>. Повтор той же операции возвращает её план.
    /// </summary>
    Task<DatasetPlan> PlanAsync(DatasetSpecification specification, string operationId, CancellationToken cancellationToken = default);

    Task<DatasetPlan> GetPlanAsync(Guid planId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Готовит вход задачи <see cref="IDatasetBuildJob"/> (её ставит ICore.Jobs.EnqueueAsync с тем же operationId): hash плана
    /// (<c>datasets.revision_conflict</c>), устаревший план (<c>datasets.plan_stale</c>), размер входа проекта
    /// (<c>datasets.input_size_mismatch</c>), пустой план (<c>datasets.empty</c>), одинаковые пиксели в разных частях
    /// (<c>datasets.split_collision</c>). Повтор операции возвращает её вход.
    /// </summary>
    Task<DatasetBuildInput> PrepareBuildAsync(Guid planId, string planHash, string operationId, CancellationToken cancellationToken = default);

    /// <summary>Вход задачи <see cref="IDatasetVerifyJob"/>: версия готова и не в архиве. Повтор операции возвращает её вход.</summary>
    Task<DatasetVerifyInput> PrepareVerifyAsync(Guid versionId, string operationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Вход задачи <see cref="IDatasetExportJob"/>: версия готова, не в архиве и не повреждена (<c>datasets.corrupt</c>), путь
    /// абсолютный .zip в существующей папке (<c>datasets.export_destination</c>). Повтор операции возвращает её вход.
    /// </summary>
    Task<DatasetExportInput> PrepareExportAsync(Guid versionId, string destinationPath, string operationId, CancellationToken cancellationToken = default);

    /// <summary>Операции вида, новые первыми (limit 1–200, before — ID последней задачи предыдущей страницы).</summary>
    Task<DatasetOperationPage> ListOperationsAsync(DatasetOperationKind kind, int limit = 50, Guid? before = null, CancellationToken cancellationToken = default);

    Task<DatasetOperation> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default);

    /// <summary>«Удалить из истории»: только завершённая операция (<c>datasets.operation_active</c>); готовая версия остаётся.</summary>
    Task DeleteOperationAsync(Guid operationId, CancellationToken cancellationToken = default);

    /// <summary>Образцы части по ID (limit 1–200, after — NextCursor предыдущей страницы той же части).</summary>
    Task<DatasetSamplePage> ListSamplesAsync(Guid versionId, DatasetSplit split, int limit = 100, string? after = null,
        CancellationToken cancellationToken = default);

    Task<DatasetSample> GetSampleAsync(Guid versionId, Guid sampleId, CancellationToken cancellationToken = default);

    /// <summary>Состав удаления: образцы, записи истории, блокировки (операции этой версии, обучение и проверки моделей).</summary>
    Task<DatasetDeletionReview> ReviewDeletionAsync(Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Удаляет версию в две фазы: строки образцов и история — в транзакции, файлы — в фоне. Паспорт версии остаётся
    /// (<see cref="GetPassportAsync"/>). Ошибки: <c>datasets.deletion_revision_conflict</c>, <c>datasets.deletion_blocked</c>.
    /// </summary>
    Task<DatasetDeletionResult> DeleteAsync(Guid versionId, string reviewRevision, CancellationToken cancellationToken = default);

    /// <summary>
    /// Папка версии для обучения (только чтение): версия готова, не в архиве (<c>datasets.archived</c>), не повреждена
    /// (<c>datasets.corrupt</c>), её размер равен входу проекта (<c>datasets.input_size_mismatch</c>), классы 0..n−1
    /// (<c>datasets.classes_invalid</c>).
    /// </summary>
    Task<DatasetTrainingSource> GetTrainingSourceAsync(Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Переносит версию в папку задачи обучения: images/ и labels/ — жёсткими ссылками (копией, если ссылка невозможна),
    /// data.yaml и manifest.json — копиями. Папка не должна существовать или должна быть пустой (<c>datasets.materialize_target</c>);
    /// ею владеет вызывающий, в том числе после отмены. Проверки — как у <see cref="GetTrainingSourceAsync"/>; пока перенос идёт,
    /// версию нельзя удалить.
    /// </summary>
    Task<DatasetTrainingSource> MaterializeAsync(Guid versionId, string targetDirectory, CancellationToken cancellationToken = default);
}
