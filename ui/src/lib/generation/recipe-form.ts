import type { BackgroundSetResponseDto } from "@/api/projects/backgrounds/response-dto/background-set-response-dto";
import type { CatalogWorkspaceResponseDto } from "@/api/projects/catalog/response-dto/catalog-workspace-response-dto";
import type { VariantResponseDto } from "@/api/projects/catalog/response-dto/variant-response-dto";
import {
    defaultRecipe,
    distributeEvenly,
    eligibleClassVariants,
    generationTexts,
    maxRecipeNameLength,
    recipeClassVariantIds,
    recipeFieldError,
    weightsTotal,
    type GenerationRecipeField,
} from "@/api/projects/generation/generation-rules";
import type { GenerationRecipeFormDto, GenerationVariantWeightDto } from "@/api/projects/generation/request-dto/generation-recipe-form-dto";
import type { GenerationRecipeResponseDto } from "@/api/projects/generation/response-dto/generation-recipe-response-dto";
import type { GenerationRepeatDraftResponseDto } from "@/api/projects/generation/response-dto/generation-sample-response-dto";
import { i18n } from "@/i18n";
import { formatNumber } from "@/lib/format";
import "./locale";

/** The form mode: a new preset, a change of a saved one or a repeat of a run (snapshot settings until "Edit variants…"). */
export type RecipeFormMode =
    | { kind: "create" }
    | { kind: "edit"; recipe: GenerationRecipeResponseDto }
    | { kind: "repeat"; sourceRunId: string; notice: string; snapshot: GenerationRecipeFormDto };

/**
 * Values of the preset form. The frame size of a new preset is the project input (canvas = null), of a change and a repeat — its own.
 * weights — shares by the ID of the project variant; emptyPercent — "Empty frames" in percent.
 */
export interface RecipeFormValues {
    mode: RecipeFormMode;
    name: string;
    description: string;
    canvas: { width: number; height: number } | null;
    setIds: string[];
    weights: Readonly<Record<string, number>>;
    objects: number;
    emptyPercent: number;
    scale: number;
}

export function emptyForm(): RecipeFormValues {
    return {
        mode: { kind: "create" }, name: "", description: "", canvas: null, setIds: [], weights: {},
        objects: defaultRecipe.objectsPerImage, emptyPercent: defaultRecipe.emptyPercent, scale: defaultRecipe.scale,
    };
}

/** A share 0..1 → percent without binary fraction tails (0.1 → 10). */
export function toPercent(probability: number): number {
    return Math.round(probability * 1e6) / 1e4;
}

function fromRecipe(recipe: GenerationRecipeFormDto, mode: RecipeFormMode, name = recipe.name): RecipeFormValues {
    return {
        mode, name, description: recipe.description, canvas: { width: recipe.width, height: recipe.height },
        setIds: [...recipe.backgroundSetIds],
        weights: Object.fromEntries(recipe.variantWeights.map(weight => [weight.variantId, weight.percentage])),
        objects: recipe.objectsPerImage, emptyPercent: toPercent(recipe.emptyProbability), scale: recipe.scale,
    };
}

/** "Edit": the form with the preset settings; saving sends its revision. */
export function formForEdit(recipe: GenerationRecipeResponseDto): RecipeFormValues {
    return fromRecipe(recipe, { kind: "edit", recipe });
}

/** "Set up a repeat": settings of the run snapshot; shares and bindings come from the snapshot until the user moves to current sources. */
export function formForRepeat(draft: GenerationRepeatDraftResponseDto): RecipeFormValues {
    return fromRecipe(draft.recipe, { kind: "repeat", sourceRunId: draft.sourceRunId, notice: i18n.t("generation:form.repeatNotice"), snapshot: draft.recipe },
        repeatName(draft.recipe.name));
}

/** "<name> · repeat" in the language of the page; the name is shortened so the whole fits 120 characters. */
export function repeatName(name: string): string {
    const suffixLength = i18n.t("generation.repeatName", { name: "" }).length;
    const stem = name.length + suffixLength > maxRecipeNameLength ? name.slice(0, Math.max(0, maxRecipeNameLength - suffixLength)).trimEnd() : name;
    return i18n.t("generation.repeatName", { name: stem });
}

/** "Edit variants by current sources": the repeat becomes an ordinary new preset with the same values. */
export function leaveSnapshot(values: RecipeFormValues): RecipeFormValues {
    return { ...values, mode: { kind: "create" } };
}

export function canvasOf(values: RecipeFormValues, input: { width: number; height: number }) {
    return values.canvas ?? input;
}

/** A row of a project variant in "Variant shares": whether it is available (it has eligible bindings) and how many classes it has. */
export interface VariantShare {
    variant: VariantResponseDto;
    eligible: boolean;
    classes: number;
    percentage: number;
}

/** Active project variants in catalog order; a variant without bindings with images is unavailable with the share 0. */
export function variantShares(workspace: CatalogWorkspaceResponseDto | undefined, weights: Readonly<Record<string, number>>): VariantShare[] {
    if (!workspace) return [];
    const bindings = eligibleClassVariants(workspace);
    return workspace.variants.filter(variant => !variant.archivedAt).map(variant => {
        const classes = new Set(bindings.filter(binding => binding.variantId === variant.id).map(binding => binding.classId)).size;
        return { variant, eligible: classes > 0, classes, percentage: classes > 0 ? weights[variant.id] ?? 0 : 0 };
    });
}

/** Shares that go to the preset: all available project variants in catalog order. */
export function formWeights(shares: readonly VariantShare[]): GenerationVariantWeightDto[] {
    return shares.filter(share => share.eligible).map(share => ({ variantId: share.variant.id, percentage: share.percentage }));
}

/** "Evenly" over the available variants. */
export function evenWeights(shares: readonly VariantShare[]): Record<string, number> {
    return Object.fromEntries(distributeEvenly(shares.filter(share => share.eligible).map(share => share.variant.id))
        .map(weight => [weight.variantId, weight.percentage]));
}

/** A share field: an integer 0–100, empty and non-numeric — 0. */
export function clampPercent(value: string | number): number {
    return Math.max(0, Math.min(100, Math.round(Number(value) || 0)));
}

/**
 * Settings for the API. In the repeat mode shares and bindings come from the run snapshot; otherwise — by the current catalog
 * (classVariantIds — variant bindings with a share above 0).
 */
export function toRecipeDto(values: RecipeFormValues, workspace: CatalogWorkspaceResponseDto | undefined, input: { width: number; height: number }): GenerationRecipeFormDto {
    const canvas = canvasOf(values, input);
    const snapshot = values.mode.kind === "repeat" ? values.mode.snapshot : undefined;
    const weights = snapshot ? snapshot.variantWeights : formWeights(variantShares(workspace, values.weights));
    return {
        name: values.name.trim(),
        description: values.description.trim(),
        width: canvas.width,
        height: canvas.height,
        backgroundSetIds: [...values.setIds],
        classVariantIds: snapshot ? [...snapshot.classVariantIds] : workspace ? recipeClassVariantIds(workspace, weights) : [],
        variantWeights: weights.map(weight => ({ ...weight })),
        objectsPerImage: values.objects,
        // An untouched percentage of a repeat keeps the exact stored probability.
        emptyProbability: snapshot && toPercent(snapshot.emptyProbability) === values.emptyPercent ? snapshot.emptyProbability : values.emptyPercent / 100,
        scale: values.scale,
    };
}

export function formTotal(values: RecipeFormValues, workspace: CatalogWorkspaceResponseDto | undefined): number {
    const snapshot = values.mode.kind === "repeat" ? values.mode.snapshot : undefined;
    return weightsTotal(snapshot ? snapshot.variantWeights : formWeights(variantShares(workspace, values.weights)));
}

/** The hint under "Objects per frame" and "Object scale": the error of the first wrong field or the general hint. */
export function objectsHint(values: RecipeFormValues): { text: string; invalid: boolean } {
    if (!Number.isSafeInteger(values.objects) || values.objects < 0) return { text: generationTexts.objectsInvalid, invalid: true };
    if (!Number.isFinite(values.scale) || values.scale <= 0) return { text: generationTexts.scaleInvalid, invalid: true };
    return { text: generationTexts.objectsHint, invalid: false };
}

/** Short texts of form fields for the host answer generation.invalid_recipe (details.field) and for the check before sending. */
export const recipeFieldTexts: Readonly<Record<GenerationRecipeField, string>> = {
    get name() { return i18n.t("generation:fields.name"); },
    get description() { return i18n.t("generation:fields.description"); },
    get canvas() { return i18n.t("generation:fields.canvas"); },
    get backgroundSetIds() { return i18n.t("generation:fields.backgroundSetIds"); },
    get classVariantIds() { return i18n.t("generation:fields.classVariantIds"); },
    get variantWeights() { return i18n.t("generation:fields.variantWeights"); },
    get objectsPerImage() { return generationTexts.objectsInvalid; },
    get emptyProbability() { return i18n.t("generation:fields.emptyProbability"); },
    get scale() { return generationTexts.scaleInvalid; },
};

/** The first wrong field of the form (host rules) or null: "Save recipe" and "Sample frame" are available only without it. */
export function formFieldError(dto: GenerationRecipeFormDto): GenerationRecipeField | null {
    return recipeFieldError(dto);
}

/** A background set in the form: one of another size is shown but unavailable, with an explanation. */
export function backgroundSetOption(set: BackgroundSetResponseDto, canvas: { width: number; height: number }) {
    const fits = set.targetSize.width === canvas.width && set.targetSize.height === canvas.height;
    return {
        fits,
        reason: fits ? undefined
            : i18n.t("generation:form.setSizeMismatch", { setWidth: set.targetSize.width, setHeight: set.targetSize.height, width: canvas.width, height: canvas.height }),
    };
}

/** "N classes" with the plural forms of the language. */
export function classesLabel(count: number): string {
    return i18n.t("generation:form.classes", { count });
}

/** "N backgrounds" with the plural forms of the language. */
export function backgroundsLabel(count: number): string {
    return i18n.t("generation:form.backgrounds", { count, value: formatNumber(count) });
}

/** The key of form settings and the seed: the sample frame is stale when the key changed. */
export function sampleKey(dto: GenerationRecipeFormDto, seed: number): string {
    return JSON.stringify([dto, seed]);
}
