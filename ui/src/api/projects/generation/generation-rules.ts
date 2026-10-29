import { i18n } from "@/i18n";
import "@/lib/generation/locale";
import type { CatalogWorkspaceResponseDto } from "../catalog/response-dto/catalog-workspace-response-dto";
import type { GenerationRunResultDto } from "./response-dto/generation-run-response-dto";
import type { GenerationRecipeFormDto, GenerationVariantWeightDto } from "./request-dto/generation-recipe-form-dto";

/** The same limits the host checks (GenerationManager.Validate and the controller). */
export const maxRecipeNameLength = 120;
export const maxRecipeDescriptionLength = 4000;
export const canvasStep = 32;
export const maxSeed = 4_294_967_295;
/** previewSamples returns up to this many frames. */
export const maxSamples = 4;
/** Tolerance of the sum of shares: 100 ± 1e-6. */
export const weightTolerance = 1e-6;

/** Initial values of the form and the run: 1 object, 10 % empty frames, scale 1, 100 images. */
export const defaultRecipe = { objectsPerImage: 1, emptyPercent: 10, scale: 1, count: 100 } as const;

/** A field of the preset form; matches details.field of the error generation.invalid_recipe. */
export type GenerationRecipeField = "name" | "description" | "canvas" | "backgroundSetIds" | "classVariantIds" | "variantWeights" | "objectsPerImage"
    | "emptyProbability" | "scale";

/** Hints and errors of the form in the current language (read at render time). */
export const generationTexts = {
    get objectsInvalid() { return i18n.t("generation:rules.objectsInvalid"); },
    get scaleInvalid() { return i18n.t("generation:rules.scaleInvalid"); },
    get objectsHint() { return i18n.t("generation:rules.objectsHint"); },
    get emptyHint() { return i18n.t("generation:rules.emptyHint"); },
    get countInvalid() { return i18n.t("generation:rules.countInvalid"); },
    get seedInvalid() { return i18n.t("generation:rules.seedInvalid"); },
    get variantWithoutAssets() { return i18n.t("generation:rules.variantWithoutAssets"); },
    get repeatHint() { return i18n.t("generation:rules.repeatHint"); },
    get cancelled() { return i18n.t("generation:rules.cancelled"); },
    get interrupted() { return i18n.t("generation:rules.interrupted"); },
};

/** "New random seed": an integer 0–4 294 967 295 from the cryptographic generator. */
export function randomSeed(): number {
    const value = new Uint32Array(1);
    crypto.getRandomValues(value);
    return value[0];
}

/**
 * Bindings a preset can choose: available ones (neither the binding, nor the class, nor the project variant is archived) with images.
 * A project variant without such bindings shows the hint "Add images to the classes" and the share 0.
 */
export function eligibleClassVariants(workspace: CatalogWorkspaceResponseDto) {
    return workspace.classVariants.filter(binding => binding.available && binding.assetCount > 0);
}

/** classVariantIds of a preset: eligible variant bindings with a share above 0, in catalog order (classes, then variants). */
export function recipeClassVariantIds(workspace: CatalogWorkspaceResponseDto, weights: readonly GenerationVariantWeightDto[]): string[] {
    const weighted = new Set(weights.filter(weight => weight.percentage > 0).map(weight => weight.variantId));
    return eligibleClassVariants(workspace).filter(binding => weighted.has(binding.variantId)).map(binding => binding.id);
}

/** "Evenly": whole shares, the sum is 100, the first variants get the remainder. */
export function distributeEvenly(variantIds: readonly string[]): GenerationVariantWeightDto[] {
    const count = variantIds.length;
    return variantIds.map((variantId, index) => ({ variantId, percentage: count === 0 ? 0 : Math.floor(100 / count) + (index < 100 % count ? 1 : 0) }));
}

/** "Total: X% of 100%". */
export function weightsTotal(weights: readonly GenerationVariantWeightDto[]): number {
    return weights.reduce((sum, weight) => sum + weight.percentage, 0);
}

/**
 * The first wrong field of the form or null — the same rules as the host: a name of 1–120 after trimming, a description up to 4000, a size
 * divisible by 32, non-empty lists without repeats, shares 0–100 summing to 100 ± 1e-6, objects — an integer ≥ 0, empty share 0..1, scale above 0.
 */
export function recipeFieldError(form: GenerationRecipeFormDto): GenerationRecipeField | null {
    const name = form.name.trim();
    if (name.length === 0 || name.length > maxRecipeNameLength) return "name";
    if (form.description.trim().length > maxRecipeDescriptionLength) return "description";
    const side = (value: number) => Number.isSafeInteger(value) && value >= canvasStep && value % canvasStep === 0;
    if (!side(form.width) || !side(form.height)) return "canvas";
    const distinct = (ids: readonly string[]) => ids.length > 0 && new Set(ids).size === ids.length;
    if (!distinct(form.backgroundSetIds)) return "backgroundSetIds";
    if (!distinct(form.classVariantIds)) return "classVariantIds";
    const weights = form.variantWeights;
    if (weights.length === 0 || !distinct(weights.map(weight => weight.variantId))
        || weights.some(weight => !Number.isFinite(weight.percentage) || weight.percentage < 0 || weight.percentage > 100)
        || Math.abs(weightsTotal(weights) - 100) > weightTolerance) return "variantWeights";
    if (!Number.isSafeInteger(form.objectsPerImage) || form.objectsPerImage < 0) return "objectsPerImage";
    if (!Number.isFinite(form.emptyProbability) || form.emptyProbability < 0 || form.emptyProbability > 1) return "emptyProbability";
    if (!Number.isFinite(form.scale) || form.scale <= 0) return "scale";
    return null;
}

/** "Number of images" — an integer from 1. */
export function isValidCount(count: number): boolean {
    return Number.isSafeInteger(count) && count >= 1;
}

/** Seed — an integer 0–4 294 967 295. */
export function isValidSeed(seed: number): boolean {
    return Number.isSafeInteger(seed) && seed >= 0 && seed <= maxSeed;
}

/** The run result in one line: "P / R images · X%" and, if any, "Frames with missing objects: N". */
export function generationRunSummary(result: GenerationRunResultDto): string {
    const percent = result.requestedCount === 0 ? 0 : Math.floor(100 * result.producedCount / result.requestedCount);
    const values = { produced: result.producedCount, requested: result.requestedCount, percent, missing: result.missingObjectFrames };
    return result.missingObjectFrames > 0 ? i18n.t("generation:rules.summaryMissing", values) : i18n.t("generation:rules.summary", values);
}
