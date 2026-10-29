import { isRecord } from "../../../transport/envelopes";
import { isCount, isDate, isDateOrNull, isId, isRevision } from "../../catalog/catalog-guards";
import type { GenerationRecipeFormDto, GenerationVariantWeightDto } from "../request-dto/generation-recipe-form-dto";

/**
 * A generation preset: form settings and revision — for changes, the archive and runs; archivedAt — "Archived". Card details
 * ("Frame size", "Variants and classes", "Background sets" with marks of deleted and archived ones) the UI builds from the catalog and background sets.
 */
export interface GenerationRecipeResponseDto extends GenerationRecipeFormDto {
    id: string;
    revision: number;
    createdAt: string;
    updatedAt: string;
    archivedAt: string | null;
}

const isFiniteNumber = (value: unknown): value is number => typeof value === "number" && Number.isFinite(value);

export function isGenerationVariantWeightDto(value: unknown): value is GenerationVariantWeightDto {
    return isRecord(value) && isId(value.variantId) && isFiniteNumber(value.percentage);
}

export function isGenerationRecipeFormDto(value: unknown): value is GenerationRecipeFormDto {
    return isRecord(value) && typeof value.name === "string" && typeof value.description === "string"
        && isCount(value.width) && isCount(value.height)
        && Array.isArray(value.backgroundSetIds) && value.backgroundSetIds.every(isId)
        && Array.isArray(value.classVariantIds) && value.classVariantIds.every(isId)
        && Array.isArray(value.variantWeights) && value.variantWeights.every(isGenerationVariantWeightDto)
        && isCount(value.objectsPerImage) && isFiniteNumber(value.emptyProbability) && isFiniteNumber(value.scale);
}

export function isGenerationRecipeResponseDto(value: unknown): value is GenerationRecipeResponseDto {
    return isGenerationRecipeFormDto(value) && isRecord(value) && isId(value.id) && isRevision(value.revision)
        && isDate(value.createdAt) && isDate(value.updatedAt) && isDateOrNull(value.archivedAt);
}

export function isGenerationRecipeList(value: unknown): value is GenerationRecipeResponseDto[] {
    return Array.isArray(value) && value.every(isGenerationRecipeResponseDto);
}

export interface GenerationRecipeDeletionResponseDto {
    recipeId: string;
}

export function isGenerationRecipeDeletionResponseDto(value: unknown): value is GenerationRecipeDeletionResponseDto {
    return isRecord(value) && isId(value.recipeId);
}
