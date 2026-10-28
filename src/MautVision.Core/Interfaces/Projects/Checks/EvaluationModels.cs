using System.Text.Json;
using System.Text.Json.Serialization;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects.Training;

namespace MautVision.Core.Interfaces.Projects.Checks;

/// <summary>Часть датасета для оценки: «Validation» или «Test».</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EvaluationSplit>))]
public enum EvaluationSplit
{
    [JsonStringEnumMemberName("val")] Validation,
    [JsonStringEnumMemberName("test")] Test,
}

/// <summary>
/// Состояние оценки для подписей: Queued «Ожидает видеокарту», Preparing «Подготовка», Running «Идёт проверка», Cancelling
/// «Остановка…», Succeeded «Готово», Failed «Ошибка», Cancelled «Остановлено», Interrupted «Прервано». Выводится из задачи и фазы.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModelEvaluationState>))]
public enum ModelEvaluationState
{
    [JsonStringEnumMemberName("queued")] Queued,
    [JsonStringEnumMemberName("preparing")] Preparing,
    [JsonStringEnumMemberName("running")] Running,
    [JsonStringEnumMemberName("cancelling")] Cancelling,
    [JsonStringEnumMemberName("succeeded")] Succeeded,
    [JsonStringEnumMemberName("failed")] Failed,
    [JsonStringEnumMemberName("cancelled")] Cancelled,
    [JsonStringEnumMemberName("interrupted")] Interrupted,
}

/// <summary>
/// «Проверить на датасете»: версия модели, версия датасета и её часть, устройство («cpu» или номер видеокарты из списка обучения),
/// batch 1–1024 (по умолчанию 16).
/// </summary>
public sealed record ModelEvaluationRequest(Guid VersionId, Guid DatasetVersionId, EvaluationSplit Split, string Device, int Batch = 16);

/// <summary>
/// Вход оценки, который готовит <see cref="IChecksManager.PrepareEvaluationAsync"/>: версия (номер, архитектура, вход, imgsz) и
/// датасет на момент постановки (DatasetVersionId — ключ блокировки удаления датасета), часть, устройство и batch.
/// </summary>
public sealed record ModelEvaluationInput(
    Guid VersionId,
    int VersionNumber,
    string Architecture,
    int Width,
    int Height,
    int ImageSize,
    Guid DatasetVersionId,
    TrainingDatasetReference Dataset,
    EvaluationSplit Split,
    string Device,
    int Batch);

/// <summary>
/// Оценка версии на части датасета: GPU-задача (ждёт в общей очереди с обучением), не возобновляемая — после завершения
/// приложения «Прервано». Ставится ICore.Jobs.EnqueueAsync с operationId запроса; ID оценки — ID задачи.
/// </summary>
[JobKind("checks.evaluate")]
public interface IModelEvaluationJob : IJobDefinition<ModelEvaluationInput>
{
}

/// <summary>Метрики класса (выход модели Index): precision, recall, mAP50, mAP50_95; только классы, у которых в части есть объекты.</summary>
public sealed record EvaluationClassMetrics(int Index, Guid? ClassId, string Name, IReadOnlyDictionary<string, double> Metrics);

/// <summary>Ошибка оценки: для checks.worker_* — сообщение самого worker на русском, иначе текст приложения по коду.</summary>
public sealed record ModelEvaluationError(string Code, string Message);

/// <summary>
/// Оценка версии модели на части датасета (Id = Job.Id): Metrics — precision, recall, mAP50, mAP50_95 как у Ultralytics
/// (model.val); ClassMetrics — по классам; ImageCount — кадров в части; Environment — окружение worker. Revision растёт с каждым
/// сохранённым изменением.
/// </summary>
public sealed record ModelEvaluation(
    JobInfo Job,
    ModelEvaluationState State,
    Guid VersionId,
    int VersionNumber,
    TrainingDatasetReference Dataset,
    EvaluationSplit Split,
    string Device,
    int Batch,
    int? ImageCount,
    IReadOnlyDictionary<string, double> Metrics,
    IReadOnlyList<EvaluationClassMetrics> ClassMetrics,
    JsonElement? Environment,
    ModelEvaluationError? Error,
    long Revision)
{
    public Guid Id => Job.Id;
}

/// <summary>Фильтр «Все проверки»: по версии, датасету и части (null — любые).</summary>
public sealed record ModelEvaluationFilter(Guid? VersionId = null, Guid? DatasetVersionId = null, EvaluationSplit? Split = null);

/// <summary>Страница оценок, новые первыми; Total — всех оценок под фильтром.</summary>
public sealed record ModelEvaluationPage(IReadOnlyList<ModelEvaluation> Items, long Total);
