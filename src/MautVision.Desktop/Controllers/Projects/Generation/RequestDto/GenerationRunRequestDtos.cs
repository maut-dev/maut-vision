namespace MautVision.Desktop.Controllers.Projects.Generation.RequestDto;

/// <summary>Запуски, новые первыми; Limit — 1–200 (по умолчанию 50), Before — ID последнего запуска предыдущей страницы.</summary>
public sealed record ListGenerationRunsRequestDto(string ProjectId, int? Limit = null, Guid? Before = null);

public sealed record GetGenerationRunRequestDto(string ProjectId, Guid RunId);

/// <summary>
/// «Сгенерировать N изображений»: RecipeRevision — ревизия пресета, которую видел пользователь; Seed — 0–4 294 967 295; Count ≥ 1;
/// OperationId — один на действие пользователя (повтор запроса вернёт тот же запуск).
/// </summary>
public sealed record StartGenerationRunRequestDto(string ProjectId, Guid RecipeId, long RecipeRevision, long Seed, int Count, string OperationId);

public sealed record DeleteGenerationRunRequestDto(string ProjectId, Guid RunId);

public sealed record GetGenerationRepeatDraftRequestDto(string ProjectId, Guid RunId);
