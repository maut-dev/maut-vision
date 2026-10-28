import type { GenerationRecipeFormDto } from "./generation-recipe-form-dto";

export interface ListGenerationRecipesRequestDto {
    projectId: string;
}

export interface GetGenerationRecipeRequestDto {
    projectId: string;
    recipeId: string;
}

export interface CreateGenerationRecipeRequestDto {
    projectId: string;
    recipe: GenerationRecipeFormDto;
}

export interface UpdateGenerationRecipeRequestDto {
    projectId: string;
    recipeId: string;
    recipe: GenerationRecipeFormDto;
    /** Ревизия пресета, которую видел пользователь. */
    revision: number;
}

export interface SetGenerationRecipeArchivedRequestDto {
    projectId: string;
    recipeId: string;
    archived: boolean;
    revision: number;
}

export interface DeleteGenerationRecipeRequestDto {
    projectId: string;
    recipeId: string;
    revision: number;
}

/** «Пример кадра»: настройки формы, seed из «Параметров запуска» (0–4 294 967 295), count 1–4 (по умолчанию 4). */
export interface PreviewGenerationSamplesRequestDto {
    projectId: string;
    recipe: GenerationRecipeFormDto;
    seed: number;
    count?: number;
}
