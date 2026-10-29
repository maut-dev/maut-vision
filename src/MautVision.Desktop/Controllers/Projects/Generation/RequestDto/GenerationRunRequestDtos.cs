namespace MautVision.Desktop.Controllers.Projects.Generation.RequestDto;

/// <summary>Runs, newest first; Limit — 1–200 (default 50), Before — ID of the last run of the previous page.</summary>
public sealed record ListGenerationRunsRequestDto(string ProjectId, int? Limit = null, Guid? Before = null);

public sealed record GetGenerationRunRequestDto(string ProjectId, Guid RunId);

/// <summary>
/// "Generate N images": RecipeRevision — the preset revision the user saw; Seed — 0–4 294 967 295; Count ≥ 1;
/// OperationId — one per user action (a repeated request returns the same run).
/// </summary>
public sealed record StartGenerationRunRequestDto(string ProjectId, Guid RecipeId, long RecipeRevision, long Seed, int Count, string OperationId);

public sealed record DeleteGenerationRunRequestDto(string ProjectId, Guid RunId);

public sealed record GetGenerationRepeatDraftRequestDto(string ProjectId, Guid RunId);
