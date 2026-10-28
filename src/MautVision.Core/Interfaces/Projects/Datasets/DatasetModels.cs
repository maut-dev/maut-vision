using System.Text.Json.Serialization;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Core.Interfaces.Projects.Images;

namespace MautVision.Core.Interfaces.Projects.Datasets;

/// <summary>Часть датасета: папки YOLO <c>train</c>, <c>val</c>, <c>test</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DatasetSplit>))]
public enum DatasetSplit
{
    [JsonStringEnumMemberName("train")] Train,
    [JsonStringEnumMemberName("val")] Validation,
    [JsonStringEnumMemberName("test")] Test,
}

/// <summary>Роль объекта без варианта класса: объект, негатив или отказ в сборке.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DatasetUnassignedVariantRole>))]
public enum DatasetUnassignedVariantRole
{
    [JsonStringEnumMemberName("target")] Target,
    [JsonStringEnumMemberName("negative")] Negative,
    [JsonStringEnumMemberName("reject")] Reject,
}

/// <summary>Роль объекта класса, не выбранного для обучения: негатив или отказ в сборке.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DatasetUnselectedClassRole>))]
public enum DatasetUnselectedClassRole
{
    [JsonStringEnumMemberName("negative")] Negative,
    [JsonStringEnumMemberName("reject")] Reject,
}

/// <summary>Verified — hash всех файлов проверен (при сборке или проверкой целостности); Corrupt — проверка нашла проблемы.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DatasetIntegrityState>))]
public enum DatasetIntegrityState
{
    [JsonStringEnumMemberName("unverified")] Unverified,
    [JsonStringEnumMemberName("verified")] Verified,
    [JsonStringEnumMemberName("corrupt")] Corrupt,
}

/// <summary>
/// Спецификация сборки. GroupIds — группы изображений; ClassIds — классы для обучения (индексы — по порядку каталога);
/// TargetVariantIds и NegativeVariantIds — варианты классов (привязки), объекты которых становятся объектами или негативами.
/// Seed — uint32; ValRatio &gt; 0, TestRatio ≥ 0, сумма &lt; 1.
/// </summary>
public sealed record DatasetSpecification(
    string Name,
    string Description,
    IReadOnlyList<Guid> GroupIds,
    IReadOnlyList<Guid> ClassIds,
    IReadOnlyList<Guid> TargetVariantIds,
    IReadOnlyList<Guid> NegativeVariantIds,
    DatasetUnassignedVariantRole UnassignedVariantRole,
    DatasetUnselectedClassRole UnselectedClassRole,
    bool IncludeReviewedEmpty,
    bool AllowPartialGeneration,
    uint Seed,
    double ValRatio,
    double TestRatio);

/// <summary>Класс версии: Index — выход модели (0..n−1 по порядку каталога), Name — название на момент сборки.</summary>
public sealed record DatasetClassEntry(int Index, Guid ClassId, string Name);

/// <summary>Вариант класса (привязка) версии: название варианта проекта на момент сборки, для показа объектов и негативов.</summary>
public sealed record DatasetVariantEntry(Guid ClassVariantId, Guid ClassId, string Name);

public sealed record DatasetSplitCounts(int Train, int Validation, int Test)
{
    public int Total => Train + Validation + Test;
}

/// <summary>Рамок класса в версии (в порядке индексов, в том числе нули).</summary>
public sealed record DatasetClassCount(int Index, Guid ClassId, string Name, int Boxes);

/// <summary>Кадров группы-источника в версии.</summary>
public sealed record DatasetGroupCount(Guid GroupId, string Name, int Images);

/// <summary>
/// Состав: кадры по частям, кадры без рамок, ручные и сгенерированные, рамки всего и по классам, негативы, группы-источники.
/// </summary>
public sealed record DatasetComposition(
    DatasetSplitCounts Images,
    int WithoutBoxes,
    int FromManual,
    int FromGenerated,
    int Boxes,
    int Negatives,
    IReadOnlyList<DatasetClassCount> BoxesPerClass,
    IReadOnlyList<DatasetGroupCount> Groups);

/// <summary>
/// Пропущенные кадры по причинам: Unreviewed — разметка не проверена; PartialGeneration — незавершённая генерация без
/// разрешения; WithoutTargets — нет объектов, а пустые кадры не включены; InvalidGeometry — рамка или ROI вне [0, 1].
/// </summary>
public sealed record DatasetPlanSkips(int Unreviewed, int PartialGeneration, int WithoutTargets, int InvalidGeometry)
{
    public int Total => Unreviewed + PartialGeneration + WithoutTargets + InvalidGeometry;
}

/// <summary>
/// Предупреждение плана: Code и Count. <c>datasets.insufficient_samples</c> — нет кадров в train, val или (при доле теста) test;
/// <c>datasets.mixed_source_splits</c> — один источник попадает в разные части (кадров в таких источниках);
/// <c>datasets.objects_as_negatives</c> — объектов архивных или удалённых классов и вариантов, ставших негативами;
/// <c>datasets.split_collision</c> — кадров с одинаковыми пикселями в разных частях (сборка невозможна).
/// </summary>
public sealed record DatasetPlanWarning(string Code, int Count);

/// <summary>
/// Результат «Проверить перед сборкой». Classes — индексы выбранных классов; Composition — кадры по частям и состав;
/// Skipped — пропущенные по причинам; Warnings; SufficientSamples; Hash — передаётся в сборку. План не копирует файлов и
/// устаревает, если изменились выбранные группы, разметка или каталог (сборка отвечает <c>datasets.plan_stale</c>).
/// </summary>
public sealed record DatasetPlan(
    Guid Id,
    DatasetSpecification Specification,
    int Width,
    int Height,
    IReadOnlyList<DatasetClassEntry> Classes,
    DatasetComposition Composition,
    DatasetPlanSkips Skipped,
    IReadOnlyList<DatasetPlanWarning> Warnings,
    bool SufficientSamples,
    string Hash,
    DateTimeOffset CreatedAt)
{
    public DatasetSplitCounts Counts => Composition.Images;
}

/// <summary>
/// Неизменяемая версия датасета (меняются только название, описание и архив). Specification — спецификация сборки, ClassMap —
/// индексы классов, Variants — названия вариантов, Composition — состав, ManifestHash — SHA-256 manifest.json. DeletedAt —
/// только у паспорта удалённой версии (<see cref="IDatasetsManager.GetPassportAsync"/>).
/// </summary>
public sealed record DatasetVersion(
    Guid Id,
    string Name,
    string Description,
    long Revision,
    DateTimeOffset? ArchivedAt,
    DatasetIntegrityState IntegrityState,
    DateTimeOffset? VerifiedAt,
    int Width,
    int Height,
    DatasetSpecification Specification,
    IReadOnlyList<DatasetClassEntry> ClassMap,
    IReadOnlyList<DatasetVariantEntry> Variants,
    DatasetComposition Composition,
    string ManifestHash,
    Guid PlanId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DeletedAt)
{
    public DatasetSplitCounts Counts => Composition.Images;
}

/// <summary>Рамка YOLO: индекс класса, класс, вариант класса и центр/размер в долях кадра.</summary>
public sealed record DatasetBox(int Index, Guid ClassId, Guid? ClassVariantId, double CenterX, double CenterY, double Width, double Height);

/// <summary>Негатив: объект, который не обучается (та же геометрия, без индекса).</summary>
public sealed record DatasetNegative(Guid ClassId, Guid? ClassVariantId, double CenterX, double CenterY, double Width, double Height);

/// <summary>
/// Образец версии: Id — ID исходного изображения; файл <c>images/&lt;split&gt;/&lt;id&gt;.jpg|.png</c> (JPEG и PNG без поворота —
/// жёсткая ссылка на исходный файл, иначе повёрнутый PNG), метка <c>labels/&lt;split&gt;/&lt;id&gt;.txt</c>. ImageHash и LabelHash —
/// SHA-256 файлов, PixelHash — хеш пикселей кадра. ResourceId — ресурс вида <see cref="ResourceKind"/> (оригинал и миниатюра).
/// </summary>
public sealed record DatasetSample(
    Guid Id,
    DatasetSplit Split,
    ImageFileFormat Format,
    int Width,
    int Height,
    long ByteSize,
    string ImageHash,
    string LabelHash,
    string PixelHash,
    long AnnotationRevision,
    ImageBox? Roi,
    IReadOnlyList<DatasetBox> Boxes,
    IReadOnlyList<DatasetNegative> Negatives,
    Guid GroupId,
    Guid ResourceId)
{
    /// <summary>Вид материала в канале ресурсов (папка files/datasets): <c>new ResourceReference(ResourceKind, sample.ResourceId)</c>.</summary>
    public const string ResourceKind = "datasets";
}

/// <summary>Страница образцов одной части по ID; NextCursor — null на последней странице.</summary>
public sealed record DatasetSamplePage(IReadOnlyList<DatasetSample> Items, string? NextCursor);

[JsonConverter(typeof(JsonStringEnumConverter<DatasetOperationKind>))]
public enum DatasetOperationKind
{
    [JsonStringEnumMemberName("build")] Build,
    [JsonStringEnumMemberName("verify")] Verify,
    [JsonStringEnumMemberName("export")] Export,
}

/// <summary>
/// Операция датасета — задача Jobs. Build: VersionId = ID задачи, Specification и PlanId — для «Настроить повтор». Verify и
/// Export: VersionId — проверяемая или экспортируемая версия; FileName — имя ZIP. VersionName — название версии (на момент
/// постановки, если версия удалена); VersionAvailable — версия готова и не удалена («Открыть датасет»).
/// </summary>
public sealed record DatasetOperation(
    JobInfo Job,
    DatasetOperationKind Kind,
    Guid VersionId,
    string VersionName,
    bool VersionAvailable,
    Guid? PlanId,
    DatasetSpecification? Specification,
    string? FileName);

/// <summary>Операции одного вида, новые первыми; Total — все операции этого вида в проекте.</summary>
public sealed record DatasetOperationPage(IReadOnlyList<DatasetOperation> Items, long Total);

/// <summary>Итог сборки (в JobInfo.Result): кадров всего и готово, из них жёсткие ссылки, копии и PNG после преобразования.</summary>
public sealed record DatasetBuildResult(Guid VersionId, int Total, int Produced, int Linked, int Copied, int Converted, DatasetSplitCounts Counts);

[JsonConverter(typeof(JsonStringEnumConverter<DatasetFileRole>))]
public enum DatasetFileRole
{
    [JsonStringEnumMemberName("manifest")] Manifest,
    [JsonStringEnumMemberName("dataYaml")] DataYaml,
    [JsonStringEnumMemberName("image")] Image,
    [JsonStringEnumMemberName("label")] Label,
}

/// <summary>
/// Проблема целостности: SampleId и Split — образец (null для manifest.json и data.yaml), Role — файл, Code —
/// <c>datasets.file_missing</c>, <c>datasets.file_changed</c> (содержимое отличается) или <c>datasets.file_unreadable</c>.
/// </summary>
public sealed record DatasetIntegrityIssue(Guid? SampleId, DatasetSplit? Split, DatasetFileRole Role, string Code);

/// <summary>
/// Итог проверки целостности (в JobInfo.Result): файлов всего и проверено, IssueCount — все проблемы, Issues — первые
/// <see cref="MaxIssues"/>; State — verified или corrupt (null, пока проверка не закончена); Bytes — прочитано; ElapsedMs.
/// </summary>
public sealed record DatasetVerifyResult(Guid VersionId, int TotalFiles, int CheckedFiles, int IssueCount, IReadOnlyList<DatasetIntegrityIssue> Issues,
    DatasetIntegrityState? State, long Bytes, long ElapsedMs)
{
    public const int MaxIssues = 500;
}

/// <summary>Итог экспорта (в JobInfo.Result): имя файла, изображений всего и записано, байт архива, длительность.</summary>
public sealed record DatasetExportResult(Guid VersionId, string FileName, int TotalImages, int ExportedImages, long Bytes, long ElapsedMs);

/// <summary>
/// Состав удаления версии: образцов, записей истории (сборка, проверки и экспорты версии) и блокировки (непустой список —
/// удаление сейчас недоступно). Revision передаётся в удаление. Паспорт версии сохраняется для моделей.
/// </summary>
public sealed record DatasetDeletionReview(Guid VersionId, string Name, long Samples, long Operations, IReadOnlyList<DeletionBlocker> Blockers, string Revision);

/// <summary>Строки удалены; CleanupPending — файлы версии ещё удаляются в фоне.</summary>
public sealed record DatasetDeletionResult(Guid VersionId, long Samples, long Operations, bool CleanupPending);

/// <summary>
/// Версия для обучения: папка YOLO (Directory) с data.yaml и manifest.json, классы 0..n−1, размер кадра и части.
/// Пути — абсолютные; у <see cref="IDatasetsManager.MaterializeAsync"/> — внутри папки задачи.
/// </summary>
public sealed record DatasetTrainingSource(
    Guid VersionId,
    string Name,
    int Width,
    int Height,
    IReadOnlyList<DatasetClassEntry> Classes,
    DatasetSplitCounts Counts,
    string ManifestHash,
    string Directory,
    string DataYamlPath,
    string ManifestPath);

/// <summary>Изменённая или удалённая (Revision = null) версия.</summary>
public sealed record DatasetEntityChange(Guid Id, long? Revision);

/// <summary>
/// Что изменилось в датасетах проекта: Versions — версии с новыми ревизиями (появилась, изменились название, архив или
/// целостность) или удалённые. Состояние и прогресс сборки, проверки и экспорта — событие JobChanged.
/// </summary>
public sealed class DatasetsChangedEventArgs(string projectId, IReadOnlyList<DatasetEntityChange> versions) : EventArgs
{
    public string ProjectId { get; } = projectId;
    public IReadOnlyList<DatasetEntityChange> Versions { get; } = versions;
}

/// <summary>
/// Счётчики готовых версий: Ready — не в архиве и не повреждённые, Archived — в архиве (в том числе повреждённые), Corrupt — повреждённые
/// вне архива. Собираемые и удалённые версии не считаются.
/// </summary>
public sealed record DatasetsSummary(int Ready, int Archived, int Corrupt);
