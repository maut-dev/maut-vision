namespace MautVision.Desktop.Controllers.Projects.Generation.RequestDto;

public sealed record ListGenerationRecipesRequestDto(string ProjectId);

public sealed record GetGenerationRecipeRequestDto(string ProjectId, Guid RecipeId);

public sealed record CreateGenerationRecipeRequestDto(string ProjectId, GenerationRecipeFormDto Recipe);

/// <summary>Revision — ревизия пресета, которую видел пользователь.</summary>
public sealed record UpdateGenerationRecipeRequestDto(string ProjectId, Guid RecipeId, GenerationRecipeFormDto Recipe, long Revision);

public sealed record SetGenerationRecipeArchivedRequestDto(string ProjectId, Guid RecipeId, bool Archived, long Revision);

public sealed record DeleteGenerationRecipeRequestDto(string ProjectId, Guid RecipeId, long Revision);

/// <summary>
/// «Пример кадра»: до 4 кадров (Count 1–4, по умолчанию 4) настроек формы с seed (0–4 294 967 295) — те же пиксели и рамки, что даст
/// запуск с этим seed.
/// </summary>
public sealed record PreviewGenerationSamplesRequestDto(string ProjectId, GenerationRecipeFormDto Recipe, long Seed, int? Count = null);
