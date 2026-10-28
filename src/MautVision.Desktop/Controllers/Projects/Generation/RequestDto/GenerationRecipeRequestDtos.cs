namespace MautVision.Desktop.Controllers.Projects.Generation.RequestDto;

public sealed record ListGenerationRecipesRequestDto(string ProjectId);

public sealed record GetGenerationRecipeRequestDto(string ProjectId, Guid RecipeId);

public sealed record CreateGenerationRecipeRequestDto(string ProjectId, GenerationRecipeFormDto Recipe);

/// <summary>Revision — the preset revision the user saw.</summary>
public sealed record UpdateGenerationRecipeRequestDto(string ProjectId, Guid RecipeId, GenerationRecipeFormDto Recipe, long Revision);

public sealed record SetGenerationRecipeArchivedRequestDto(string ProjectId, Guid RecipeId, bool Archived, long Revision);

public sealed record DeleteGenerationRecipeRequestDto(string ProjectId, Guid RecipeId, long Revision);

/// <summary>
/// "Sample frame": up to 4 frames (Count 1–4, default 4) of the form settings with a seed (0–4 294 967 295) — the same pixels and boxes that
/// a run with this seed produces.
/// </summary>
public sealed record PreviewGenerationSamplesRequestDto(string ProjectId, GenerationRecipeFormDto Recipe, long Seed, int? Count = null);
