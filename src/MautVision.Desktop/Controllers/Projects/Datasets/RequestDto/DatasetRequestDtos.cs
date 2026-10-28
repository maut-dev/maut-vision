namespace MautVision.Desktop.Controllers.Projects.Datasets.RequestDto;

/// <summary>
/// Спецификация сборки из формы. UnassignedVariantRole: target | negative | reject; UnselectedClassRole: negative | reject;
/// Seed — 0–4 294 967 295; ValRatio &gt; 0, TestRatio ≥ 0, сумма &lt; 1 (доли, не проценты). TargetVariantIds и NegativeVariantIds —
/// варианты классов (привязки); обычно целевые — активные варианты выбранных классов, негативные — архивные.
/// </summary>
public sealed record DatasetSpecificationDto(
    string Name,
    string Description,
    Guid[] GroupIds,
    Guid[] ClassIds,
    Guid[] TargetVariantIds,
    Guid[] NegativeVariantIds,
    string UnassignedVariantRole,
    string UnselectedClassRole,
    bool IncludeReviewedEmpty,
    bool AllowPartialGeneration,
    long Seed,
    double ValRatio,
    double TestRatio);

/// <summary>Готовые версии, новые первыми; IncludeArchived — «Показать архив датасетов» (по умолчанию да).</summary>
public sealed record ListDatasetsRequestDto(string ProjectId, bool? IncludeArchived = null);

public sealed record GetDatasetRequestDto(string ProjectId, Guid VersionId);

/// <summary>Паспорт версии (и удалённой) для моделей.</summary>
public sealed record GetDatasetPassportRequestDto(string ProjectId, Guid VersionId);

/// <summary>«Изменить»: название и описание с ревизией, которую видел пользователь.</summary>
public sealed record UpdateDatasetRequestDto(string ProjectId, Guid VersionId, string Name, string Description, long Revision);

/// <summary>«В архив» / «Восстановить».</summary>
public sealed record SetDatasetArchivedRequestDto(string ProjectId, Guid VersionId, bool Archived, long Revision);

/// <summary>«Проверить перед сборкой»: OperationId — новый на каждое нажатие (повтор запроса вернёт тот же план).</summary>
public sealed record PlanDatasetRequestDto(string ProjectId, DatasetSpecificationDto Specification, string OperationId);

public sealed record GetDatasetPlanRequestDto(string ProjectId, Guid PlanId);

/// <summary>«Собрать датасет»: план и его hash из ответа plan; OperationId — новый на действие (повтор вернёт ту же сборку).</summary>
public sealed record StartDatasetBuildRequestDto(string ProjectId, Guid PlanId, string PlanHash, string OperationId);

/// <summary>«Проверить целостность».</summary>
public sealed record StartDatasetVerifyRequestDto(string ProjectId, Guid VersionId, string OperationId);

/// <summary>«Экспорт ZIP», шаг 1: нативный «Сохранить как» с именем «&lt;название&gt;.zip»; ответ — токен выбора.</summary>
public sealed record SelectDatasetExportRequestDto(string ProjectId, Guid VersionId);

/// <summary>«Экспорт ZIP», шаг 2: DestinationToken — из selectExportDestination; OperationId — новый на действие.</summary>
public sealed record StartDatasetExportRequestDto(string ProjectId, Guid VersionId, string DestinationToken, string OperationId);

/// <summary>Операции вида build | verify | export, новые первыми; Limit 1–200 (по умолчанию 50), Before — ID последней предыдущей страницы.</summary>
public sealed record ListDatasetOperationsRequestDto(string ProjectId, string Kind, int? Limit = null, Guid? Before = null);

public sealed record GetDatasetOperationRequestDto(string ProjectId, Guid OperationId);

/// <summary>«Удалить из истории» завершённой операции.</summary>
public sealed record DeleteDatasetOperationRequestDto(string ProjectId, Guid OperationId);

/// <summary>Образцы части train | val | test по ID; Limit 1–200 (по умолчанию 100), After — NextCursor предыдущей страницы.</summary>
public sealed record ListDatasetSamplesRequestDto(string ProjectId, Guid VersionId, string Split, int? Limit = null, string? After = null);

public sealed record GetDatasetSampleRequestDto(string ProjectId, Guid VersionId, Guid SampleId);

public sealed record ReviewDatasetDeletionRequestDto(string ProjectId, Guid VersionId);

/// <summary>Удаление с Revision из reviewDeletion.</summary>
public sealed record DeleteDatasetRequestDto(string ProjectId, Guid VersionId, string Revision);
