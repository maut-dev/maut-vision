using System.Text.Json;
using System.Text.Json.Serialization;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects.Datasets;

namespace MautVision.Core.Interfaces.Projects.Training;

/// <summary>
/// Состояние запуска для подписей экранов: Queued «Ожидает видеокарту», Preparing «Подготовка», Training «Обучение», Cancelling
/// «Отмена…», Succeeded «Готово», Failed «Ошибка», Cancelled «Отменено», Interrupted «Прервано». Выводится из задачи и фазы запуска.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TrainingRunState>))]
public enum TrainingRunState
{
    [JsonStringEnumMemberName("queued")] Queued,
    [JsonStringEnumMemberName("preparing")] Preparing,
    [JsonStringEnumMemberName("training")] Training,
    [JsonStringEnumMemberName("cancelling")] Cancelling,
    [JsonStringEnumMemberName("succeeded")] Succeeded,
    [JsonStringEnumMemberName("failed")] Failed,
    [JsonStringEnumMemberName("cancelled")] Cancelled,
    [JsonStringEnumMemberName("interrupted")] Interrupted,
}

/// <summary>
/// Датасет запуска и версии модели: VersionId — версия датасета (её паспорт остаётся после удаления), Name — название на момент
/// запуска, ManifestHash — SHA-256 manifest.json, Classes — классы 0..n−1 датасета, Counts — кадры по частям.
/// </summary>
public sealed record TrainingDatasetReference(Guid VersionId, string Name, string ManifestHash, IReadOnlyList<DatasetClassEntry> Classes, DatasetSplitCounts Counts);

/// <summary>«Начать обучение»: версия датасета, параметры (ключи протокола worker, отсутствующий — подобранное значение) и пресет, если выбран.</summary>
public sealed record TrainingRunRequest(Guid DatasetVersionId, IReadOnlyDictionary<string, JsonElement> Parameters, string? PresetName = null);

/// <summary>
/// Ход обучения: Stage — стадия worker (validating, loading_model, training, finalizing, exporting); Epoch из Epochs и Batch из
/// Batches; Metrics — потери и метрики последней эпохи («живые метрики»).
/// </summary>
public sealed record TrainingRunProgress(string? Stage, int? Epoch, int? Epochs, int? Batch, int? Batches, IReadOnlyDictionary<string, double> Metrics);

/// <summary>Ошибка запуска: код и текст — для кодов worker (training.worker_*) это сообщение самого worker на русском.</summary>
public sealed record TrainingRunError(string Code, string Message);

/// <summary>
/// Запуск обучения — задача <see cref="ITrainingRunJob"/> (Id = Job.Id). Architecture и ImageSize — модель и вход проекта на
/// момент запуска; Parameters — запрошенные (полные, с подобранными значениями); EffectiveParams — фактические от worker (null,
/// пока не пришли); Environment — окружение worker (python, torch, ultralytics, cuda, deviceName); EpochCount и LogCount — записей
/// истории эпох и журнала; ResultVersionId — версия модели после «Готово». Revision растёт с сохранёнными изменениями запуска.
/// </summary>
public sealed record TrainingRun(
    JobInfo Job,
    TrainingRunState State,
    TrainingDatasetReference Dataset,
    string Architecture,
    InputSize Input,
    int ImageSize,
    IReadOnlyDictionary<string, JsonElement> Parameters,
    string? PresetName,
    JsonElement? EffectiveParams,
    TrainingRunProgress Progress,
    int EpochCount,
    long LogCount,
    JsonElement? Environment,
    TrainingRunError? Error,
    Guid? ResultVersionId,
    long Revision)
{
    public Guid Id => Job.Id;
}

/// <summary>Страница запусков, новые первыми; Total — все запуски проекта.</summary>
public sealed record TrainingRunPage(IReadOnlyList<TrainingRun> Items, long Total);

/// <summary>Эпоха истории: потери обучения, метрики и потери проверки, lr и fitness (null без проверки в этой эпохе).</summary>
public sealed record TrainingEpoch(int Epoch, IReadOnlyDictionary<string, double> Losses, IReadOnlyDictionary<string, double> Metrics, double? Lr, double? Fitness);

/// <summary>Страница истории эпох по возрастанию; NextAfter — номер эпохи для следующей страницы (null — последняя).</summary>
public sealed record TrainingEpochPage(IReadOnlyList<TrainingEpoch> Items, int Total, int? NextAfter);

/// <summary>Вид записи журнала: log — строка консоли обучения, stage — стадия worker, warning — предупреждение, system — сообщение приложения.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TrainingLogKind>))]
public enum TrainingLogKind
{
    [JsonStringEnumMemberName("log")] Log,
    [JsonStringEnumMemberName("stage")] Stage,
    [JsonStringEnumMemberName("warning")] Warning,
    [JsonStringEnumMemberName("system")] System,
}

public sealed record TrainingLogEntry(long Sequence, DateTimeOffset At, TrainingLogKind Kind, string Message);

/// <summary>Записи журнала по возрастанию Sequence; Total — все записи; NextAfter — для следующей страницы (null — последняя).</summary>
public sealed record TrainingLogPage(IReadOnlyList<TrainingLogEntry> Items, long Total, long? NextAfter);

/// <summary>
/// Класс выхода модели: Index — номер выхода; ClassId — класс проекта (null у объединённого класса singleCls); SourceClassIds —
/// классы проекта, которые он представляет (один, или все выбранные при singleCls).
/// </summary>
public sealed record ModelClass(int Index, Guid? ClassId, string Name, IReadOnlyList<Guid> SourceClassIds);

/// <summary>Вид файла версии: weights_pt (best.pt), onnx (best.onnx), plot (график или пример), log (журнал обучения).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModelFileKind>))]
public enum ModelFileKind
{
    [JsonStringEnumMemberName("weights_pt")] WeightsPt,
    [JsonStringEnumMemberName("onnx")] Onnx,
    [JsonStringEnumMemberName("plot")] Plot,
    [JsonStringEnumMemberName("log")] Log,
}

/// <summary>
/// Файл версии в files/models/&lt;versionId&gt;/: Name — путь внутри папки версии (best.pt, plots/results.png); ResourceId —
/// ресурс вида <see cref="ModelVersion.ResourceKind"/> (картинки отдаёт канал ресурсов).
/// </summary>
public sealed record ModelVersionFile(ModelFileKind Kind, string Name, long ByteSize, string Sha256, Guid ResourceId);

/// <summary>
/// Версия модели (паспорт для экрана «Модели»): Number — номер в проекте с 1; RunId — запуск; Dataset — датасет и его паспорт;
/// Parameters — запрошенные, EffectiveParams — фактические; EpochsCompleted и StoppedEarly — ранняя остановка; BestEpoch — эпоха с
/// наибольшим fitness; Classes — в порядке выходов модели; Metrics — проверка best.pt; Files с SHA-256; Notes — предупреждения
/// worker «код: текст»; Environment — окружение обучения; IsWorking — «Рабочая» (первая версия — автоматически).
/// </summary>
public sealed record ModelVersion(
    Guid Id,
    int Number,
    Guid RunId,
    TrainingDatasetReference Dataset,
    string Architecture,
    InputSize Input,
    int ImageSize,
    IReadOnlyDictionary<string, JsonElement> Parameters,
    JsonElement EffectiveParams,
    int EpochsCompleted,
    bool StoppedEarly,
    int? BestEpoch,
    IReadOnlyList<ModelClass> Classes,
    IReadOnlyDictionary<string, double> Metrics,
    IReadOnlyList<ModelVersionFile> Files,
    IReadOnlyList<string> Notes,
    JsonElement Environment,
    bool IsWorking,
    long Revision,
    DateTimeOffset CreatedAt)
{
    /// <summary>Вид материала в канале ресурсов (папка files/models): <c>new ResourceReference(ResourceKind, file.ResourceId)</c>.</summary>
    public const string ResourceKind = "models";
}

/// <summary>
/// Состав удаления версии: Evaluations — её оценки на датасетах (удаляются вместе с ней), Bytes — файлы версии; Blockers —
/// причины, по которым удалить сейчас нельзя: <c>training.version_working</c> (рабочая версия), <c>checks.evaluation_active</c>
/// (оценка в очереди или идёт), <c>checks.live_active</c> (идёт живая проверка), <c>training.export_active</c> (выгрузка ZIP).
/// </summary>
public sealed record ModelVersionDeletionReview(Guid VersionId, int Number, bool IsWorking, int Evaluations, long Bytes,
    IReadOnlyList<Catalog.DeletionBlocker> Blockers);

/// <summary>Версия удалена; Evaluations — удалено оценок; CleanupPending — папка файлов ещё удаляется в фоне.</summary>
public sealed record ModelVersionDeletionResult(Guid VersionId, int Number, int Evaluations, bool CleanupPending);

/// <summary>Изменённая или удалённая (Revision = null) сущность обучения.</summary>
public sealed record TrainingEntityChange(Guid Id, long? Revision);

/// <summary>
/// Что изменилось в обучении проекта: Runs — запуски с новой ревизией (ход, эпохи, журнал, фактические параметры, итог; сливается
/// не чаще раза в 500 мс); Versions — версии моделей (появилась, сменилась «Рабочая», удалена — Revision = null). Состояние задачи —
/// JobChanged.
/// </summary>
public sealed class TrainingChangedEventArgs(string projectId, IReadOnlyList<TrainingEntityChange> runs, IReadOnlyList<TrainingEntityChange> versions) : EventArgs
{
    public string ProjectId { get; } = projectId;
    public IReadOnlyList<TrainingEntityChange> Runs { get; } = runs;
    public IReadOnlyList<TrainingEntityChange> Versions { get; } = versions;
}
