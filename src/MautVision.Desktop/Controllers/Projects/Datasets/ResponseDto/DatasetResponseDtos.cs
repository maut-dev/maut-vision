using MautVision.Desktop.Controllers.Jobs.ResponseDto;
using MautVision.Desktop.Controllers.Projects.Catalog.ResponseDto;
using MautVision.Desktop.Controllers.Projects.Datasets.RequestDto;
using MautVision.Desktop.Controllers.Projects.Images.ResponseDto;

namespace MautVision.Desktop.Controllers.Projects.Datasets.ResponseDto;

/// <summary>Класс версии: Index — выход модели (0..n−1), Name — название на момент сборки («Сохранённый порядок классов: 0: A, 1: B»).</summary>
public sealed record DatasetClassEntryDto(int Index, Guid ClassId, string Name);

/// <summary>Вариант класса версии: название варианта проекта на момент сборки (для подписей объектов и негативов).</summary>
public sealed record DatasetVariantEntryDto(Guid ClassVariantId, Guid ClassId, string Name);

/// <summary>Кадры по частям: «Train / Validation / Test».</summary>
public sealed record DatasetSplitCountsDto(int Train, int Val, int Test, int Total);

public sealed record DatasetClassCountDto(int Index, Guid ClassId, string Name, int Boxes);

public sealed record DatasetGroupCountDto(Guid GroupId, string Name, int Images);

/// <summary>Состав: части, кадры без рамок, ручные и сгенерированные, рамки всего и по классам, негативы, группы-источники.</summary>
public sealed record DatasetCompositionDto(DatasetSplitCountsDto Images, int WithoutBoxes, int FromManual, int FromGenerated, int Boxes, int Negatives,
    DatasetClassCountDto[] BoxesPerClass, DatasetGroupCountDto[] Groups);

/// <summary>Пропущено кадров по причинам: не проверены, незавершённая генерация, без объектов (пустые не включены), рамка вне кадра.</summary>
public sealed record DatasetPlanSkipsDto(int Unreviewed, int PartialGeneration, int WithoutTargets, int InvalidGeometry, int Total);

/// <summary>Предупреждение плана: код, число (кадров или объектов) и готовый текст.</summary>
public sealed record DatasetPlanWarningDto(string Code, int Count);

/// <summary>
/// План («Проверить перед сборкой»): Counts — Train/Validation/Test, Skipped — пропущенные по причинам, Warnings, SufficientSamples;
/// Buildable — «Собрать датасет» доступна (есть кадры и нет одинаковых пикселей в разных частях); Hash — передаётся в startBuild.
/// </summary>
public sealed record DatasetPlanResponseDto(
    Guid Id,
    DatasetSpecificationDto Specification,
    int Width,
    int Height,
    DatasetClassEntryDto[] Classes,
    DatasetSplitCountsDto Counts,
    DatasetCompositionDto Composition,
    DatasetPlanSkipsDto Skipped,
    DatasetPlanWarningDto[] Warnings,
    bool SufficientSamples,
    bool Buildable,
    string Hash,
    DateTimeOffset CreatedAt);

/// <summary>
/// Версия датасета. IntegrityState: verified | corrupt | unverified («Целостность: проверена / нарушена / не проверена»);
/// InputSizeMatches — размер равен входу проекта (иначе «Размер отличается от входа проекта», обучение недоступно); Revision —
/// для изменения и архива; DeletedAt — только у паспорта удалённой версии.
/// </summary>
public sealed record DatasetVersionResponseDto(
    Guid Id,
    string Name,
    string Description,
    long Revision,
    DateTimeOffset? ArchivedAt,
    string IntegrityState,
    DateTimeOffset? VerifiedAt,
    int Width,
    int Height,
    bool InputSizeMatches,
    DatasetSpecificationDto Specification,
    DatasetClassEntryDto[] ClassMap,
    DatasetVariantEntryDto[] Variants,
    DatasetSplitCountsDto Counts,
    DatasetCompositionDto Composition,
    string ManifestHash,
    Guid PlanId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DeletedAt);

/// <summary>Рамка YOLO: индекс, класс, вариант класса, центр и размер в долях кадра.</summary>
public sealed record DatasetBoxDto(int Index, Guid ClassId, Guid? ClassVariantId, double CenterX, double CenterY, double Width, double Height);

/// <summary>Негатив: та же геометрия без индекса (рисуется #f6b44c пунктиром).</summary>
public sealed record DatasetNegativeDto(Guid ClassId, Guid? ClassVariantId, double CenterX, double CenterY, double Width, double Height);

/// <summary>
/// Образец: Id — ID исходного изображения, Split — train | val | test, Format — jpeg | png (файл образца), Roi — область в долях
/// от левого верхнего угла или null; ImageUrl и ThumbnailUrl — ресурсы файла образца (действуют 10 минут для этой страницы).
/// </summary>
public sealed record DatasetSampleResponseDto(
    Guid Id,
    string Split,
    string Format,
    int Width,
    int Height,
    long ByteSize,
    string ImageHash,
    string LabelHash,
    string PixelHash,
    long AnnotationRevision,
    ImageBoxResponseDto? Roi,
    DatasetBoxDto[] Boxes,
    DatasetNegativeDto[] Negatives,
    Guid GroupId,
    string ImageUrl,
    string ThumbnailUrl);

public sealed record DatasetSamplePageResponseDto(DatasetSampleResponseDto[] Items, string? NextCursor);

/// <summary>
/// Операция: Job — снимок задачи (datasets.build, datasets.verify, datasets.export) с прогрессом processed/total и итогом;
/// Kind — build | verify | export; VersionId и VersionName — версия (у сборки — создаваемая); VersionAvailable — «Открыть датасет»;
/// PlanId и Specification — у сборки («Настроить повтор»); FileName — имя ZIP у экспорта.
/// </summary>
public sealed record DatasetOperationResponseDto(
    JobResponseDto Job,
    string Kind,
    Guid VersionId,
    string VersionName,
    bool VersionAvailable,
    Guid? PlanId,
    DatasetSpecificationDto? Specification,
    string? FileName);

/// <summary>Операции вида, новые первыми; Total — «Проверки (N)».</summary>
public sealed record DatasetOperationPageResponseDto(DatasetOperationResponseDto[] Items, long Total);

public sealed record DatasetOperationDeletionResponseDto(Guid OperationId);

/// <summary>Выбор «Сохранить как»: Token — для startExport, FileName — выбранное имя файла; оба null, если окно закрыто.</summary>
public sealed record DatasetExportSelectionResponseDto(string? Token, string? FileName);

/// <summary>Состав удаления: образцов, записей истории; непустой Blockers — удаление недоступно (строки диалога строит страница по кодам).</summary>
public sealed record DatasetDeletionReviewResponseDto(Guid VersionId, string Name, long Samples, long Operations, DeletionBlockerResponseDto[] Blockers,
    string Revision);

/// <summary>Удалено; CleanupPending — файлы версии ещё удаляются в фоне. Паспорт версии сохранён для моделей.</summary>
public sealed record DatasetDeletionResultResponseDto(Guid VersionId, long Samples, long Operations, bool CleanupPending);

/// <summary>Итог сборки (job.result): кадров всего и готово, из них жёсткие ссылки, копии и PNG после преобразования; части версии.</summary>
public sealed record DatasetBuildResultDto(Guid VersionId, int Total, int Produced, int Linked, int Copied, int Converted, DatasetSplitCountsDto Counts);

/// <summary>Проблема целостности: образец и часть (null для manifest.json и data.yaml), Role — manifest | dataYaml | image | label, код и текст.</summary>
public sealed record DatasetIntegrityIssueDto(Guid? SampleId, string? Split, string Role, string Code);

/// <summary>
/// Итог проверки целостности (job.result): «Проверено файлов: CheckedFiles из TotalFiles», IssueCount — «Обнаруженные проблемы (n)»,
/// Issues — первые 500; State — verified | corrupt (null, пока не закончена или отменена); Bytes и ElapsedMs — для скорости.
/// </summary>
public sealed record DatasetVerifyResultDto(Guid VersionId, int TotalFiles, int CheckedFiles, int IssueCount, DatasetIntegrityIssueDto[] Issues, string? State,
    long Bytes, long ElapsedMs);

/// <summary>Итог экспорта (job.result): «Экспортировано изображений: ExportedImages из TotalImages», имя файла, размер архива, длительность.</summary>
public sealed record DatasetExportResultDto(Guid VersionId, string FileName, int TotalImages, int ExportedImages, long Bytes, long ElapsedMs);

/// <summary>Изменённая или удалённая (Revision = null) версия.</summary>
public sealed record DatasetEntityChangeDto(Guid Id, long? Revision);

/// <summary>Событие datasets.changed: версии с новыми ревизиями (появилась, изменена, проверена) или удалённые; перечитать get / list.</summary>
public sealed record DatasetsChangedEventDto(string ProjectId, DatasetEntityChangeDto[] Versions);
