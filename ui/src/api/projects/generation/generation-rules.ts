import type { CatalogWorkspaceResponseDto } from "../catalog/response-dto/catalog-workspace-response-dto";
import type { GenerationRunResultDto } from "./response-dto/generation-run-response-dto";
import type { GenerationRecipeFormDto, GenerationVariantWeightDto } from "./request-dto/generation-recipe-form-dto";

/** Те же пределы, что проверяет host (GenerationManager.Validate и контроллер). */
export const maxRecipeNameLength = 120;
export const maxRecipeDescriptionLength = 4000;
export const canvasStep = 32;
export const maxSeed = 4_294_967_295;
/** До стольких кадров возвращает previewSamples. */
export const maxSamples = 4;
/** Допуск суммы долей: 100 ± 1e-6. */
export const weightTolerance = 1e-6;

/** Начальные значения формы и запуска: 1 объект, 10 % пустых кадров, масштаб 1, 100 изображений. */
export const defaultRecipe = { objectsPerImage: 1, emptyPercent: 10, scale: 1, count: 100 } as const;

/** Поле формы пресета; совпадает с details.field ошибки generation.invalid_recipe. */
export type GenerationRecipeField = "name" | "description" | "canvas" | "backgroundSetIds" | "classVariantIds" | "variantWeights" | "objectsPerImage"
    | "emptyProbability" | "scale";

/** Тексты подсказок и ошибок формы. */
export const generationTexts = {
    objectsInvalid: "Объектов на кадр — целое число от 0.",
    scaleInvalid: "Масштаб объекта — число больше 0.",
    objectsHint: "0 объектов — только фоны без разметки. Масштаб 1 — объект может занять весь кадр.",
    emptyHint: "Такая часть кадров останется без объектов — это помогает модели не находить объекты на пустом фоне.",
    countInvalid: "Укажите целое количество от 1.",
    seedInvalid: "Дополнительно: seed — целое число от 0 до 4 294 967 295.",
    variantWithoutAssets: "Добавьте изображения к классам",
    repeatHint: "Доли, варианты и размер взяты из снимка запуска. Удалённые и архивные источники могут помешать сохранению.",
    cancelled: "Генерация отменена. Сохранённые результаты доступны, если группа результата сохранилась.",
    interrupted: "Генерация прервана закрытием приложения. Сохранённые результаты доступны, если группа результата сохранилась.",
} as const;

/** «Новый случайный seed»: целое 0–4 294 967 295 из криптографического генератора. */
export function randomSeed(): number {
    const value = new Uint32Array(1);
    crypto.getRandomValues(value);
    return value[0];
}

/**
 * Привязки, которые может выбрать пресет: доступные (ни привязка, ни класс, ни вариант проекта не в архиве) и с изображениями.
 * Вариант проекта без таких привязок показывается с подсказкой «Добавьте изображения к классам» и долей 0.
 */
export function eligibleClassVariants(workspace: CatalogWorkspaceResponseDto) {
    return workspace.classVariants.filter(binding => binding.available && binding.assetCount > 0);
}

/** classVariantIds пресета: подходящие привязки вариантов с долей больше 0, в порядке каталога (классы, затем варианты). */
export function recipeClassVariantIds(workspace: CatalogWorkspaceResponseDto, weights: readonly GenerationVariantWeightDto[]): string[] {
    const weighted = new Set(weights.filter(weight => weight.percentage > 0).map(weight => weight.variantId));
    return eligibleClassVariants(workspace).filter(binding => weighted.has(binding.variantId)).map(binding => binding.id);
}

/** «Поровну»: целые доли, сумма 100, первые варианты получают остаток. */
export function distributeEvenly(variantIds: readonly string[]): GenerationVariantWeightDto[] {
    const count = variantIds.length;
    return variantIds.map((variantId, index) => ({ variantId, percentage: count === 0 ? 0 : Math.floor(100 / count) + (index < 100 % count ? 1 : 0) }));
}

/** «Всего: X% из 100%». */
export function weightsTotal(weights: readonly GenerationVariantWeightDto[]): number {
    return weights.reduce((sum, weight) => sum + weight.percentage, 0);
}

/**
 * Первое неверное поле формы или null — те же правила, что у host: название 1–120 после обрезки, описание до 4000, размер кратен 32,
 * непустые списки без повторов, доли 0–100 с суммой 100 ± 1e-6, объектов — целое ≥ 0, доля пустых 0..1, масштаб больше 0.
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

/** «Количество изображений» — целое от 1. */
export function isValidCount(count: number): boolean {
    return Number.isSafeInteger(count) && count >= 1;
}

/** Seed — целое 0–4 294 967 295. */
export function isValidSeed(seed: number): boolean {
    return Number.isSafeInteger(seed) && seed >= 0 && seed <= maxSeed;
}

/** Итог запуска одной строкой: «P / R изображений · X%» и, если есть, «Кадров с недостающими объектами: N». */
export function generationRunSummary(result: GenerationRunResultDto): string {
    const percent = result.requestedCount === 0 ? 0 : Math.floor(100 * result.producedCount / result.requestedCount);
    const base = `${result.producedCount} / ${result.requestedCount} изображений · ${percent}%`;
    return result.missingObjectFrames > 0 ? `${base}. Кадров с недостающими объектами: ${result.missingObjectFrames}` : base;
}
