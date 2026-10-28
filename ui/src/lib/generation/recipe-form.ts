import { backgroundsWord } from "@/api/projects/backgrounds/background-import-rules";
import type { BackgroundSetResponseDto } from "@/api/projects/backgrounds/response-dto/background-set-response-dto";
import type { CatalogWorkspaceResponseDto } from "@/api/projects/catalog/response-dto/catalog-workspace-response-dto";
import type { VariantResponseDto } from "@/api/projects/catalog/response-dto/variant-response-dto";
import {
    defaultRecipe,
    distributeEvenly,
    eligibleClassVariants,
    generationTexts,
    recipeClassVariantIds,
    recipeFieldError,
    weightsTotal,
    type GenerationRecipeField,
} from "@/api/projects/generation/generation-rules";
import type { GenerationRecipeFormDto, GenerationVariantWeightDto } from "@/api/projects/generation/request-dto/generation-recipe-form-dto";
import type { GenerationRecipeResponseDto } from "@/api/projects/generation/response-dto/generation-recipe-response-dto";
import type { GenerationRepeatDraftResponseDto } from "@/api/projects/generation/response-dto/generation-sample-response-dto";
import { locale } from "@/lib/format";

/** Режим формы: новый пресет, изменение сохранённого или повтор запуска (настройки снимка до «Редактировать варианты…»). */
export type RecipeFormMode =
    | { kind: "create" }
    | { kind: "edit"; recipe: GenerationRecipeResponseDto }
    | { kind: "repeat"; sourceRunId: string; notice: string; snapshot: GenerationRecipeFormDto };

/**
 * Значения формы пресета. Размер кадра у нового пресета — вход проекта (canvas = null), у изменения и повтора — свой.
 * weights — доли по ID варианта проекта; emptyPercent — «Доля пустых кадров» в процентах.
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

/** Доля 0..1 → проценты без хвостов двоичной дроби (0.1 → 10). */
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

/** «Изменить»: форма с настройками пресета; сохранение отправляет его ревизию. */
export function formForEdit(recipe: GenerationRecipeResponseDto): RecipeFormValues {
    return fromRecipe(recipe, { kind: "edit", recipe });
}

/** The notice above the form of «Настроить повтор» (the previous text without the removed thread count). */
const repeatNotice = "Настройки сохранённого запуска перенесены в форму. Сохраните новый пресет и запустите его.";

/** «Настроить повтор»: настройки снимка запуска; доли и привязки — из снимка, пока пользователь не перейдёт к текущим источникам. */
export function formForRepeat(draft: GenerationRepeatDraftResponseDto): RecipeFormValues {
    return fromRecipe(draft.recipe, { kind: "repeat", sourceRunId: draft.sourceRunId, notice: repeatNotice, snapshot: draft.recipe });
}

/** «Редактировать варианты по текущим источникам»: повтор становится обычным новым пресетом с теми же значениями. */
export function leaveSnapshot(values: RecipeFormValues): RecipeFormValues {
    return { ...values, mode: { kind: "create" } };
}

export function canvasOf(values: RecipeFormValues, input: { width: number; height: number }) {
    return values.canvas ?? input;
}

/** Строка варианта проекта в «Доли вариантов»: доступен ли он (есть подходящие привязки) и сколько у него классов. */
export interface VariantShare {
    variant: VariantResponseDto;
    eligible: boolean;
    classes: number;
    percentage: number;
}

/** Активные варианты проекта в порядке каталога; вариант без привязок с изображениями — недоступен и с долей 0. */
export function variantShares(workspace: CatalogWorkspaceResponseDto | undefined, weights: Readonly<Record<string, number>>): VariantShare[] {
    if (!workspace) return [];
    const bindings = eligibleClassVariants(workspace);
    return workspace.variants.filter(variant => !variant.archivedAt).map(variant => {
        const classes = new Set(bindings.filter(binding => binding.variantId === variant.id).map(binding => binding.classId)).size;
        return { variant, eligible: classes > 0, classes, percentage: classes > 0 ? weights[variant.id] ?? 0 : 0 };
    });
}

/** Доли, которые уходят в пресет: все доступные варианты проекта в порядке каталога. */
export function formWeights(shares: readonly VariantShare[]): GenerationVariantWeightDto[] {
    return shares.filter(share => share.eligible).map(share => ({ variantId: share.variant.id, percentage: share.percentage }));
}

/** «Поровну» по доступным вариантам. */
export function evenWeights(shares: readonly VariantShare[]): Record<string, number> {
    return Object.fromEntries(distributeEvenly(shares.filter(share => share.eligible).map(share => share.variant.id))
        .map(weight => [weight.variantId, weight.percentage]));
}

/** Поле доли: целое 0–100, пустое и нечисловое — 0. */
export function clampPercent(value: string | number): number {
    return Math.max(0, Math.min(100, Math.round(Number(value) || 0)));
}

/**
 * Настройки для API. В режиме повтора доли и привязки берутся из снимка запуска; иначе — по текущему каталогу
 * (classVariantIds — привязки вариантов с долей больше 0).
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

/** Подсказка под «Объектов на кадр» и «Масштаб объекта»: ошибка первого неверного поля или общая подсказка. */
export function objectsHint(values: RecipeFormValues): { text: string; invalid: boolean } {
    if (!Number.isSafeInteger(values.objects) || values.objects < 0) return { text: generationTexts.objectsInvalid, invalid: true };
    if (!Number.isFinite(values.scale) || values.scale <= 0) return { text: generationTexts.scaleInvalid, invalid: true };
    return { text: generationTexts.objectsHint, invalid: false };
}

/** Короткие тексты полей формы для ответа host generation.invalid_recipe (details.field) и для проверки до отправки. */
export const recipeFieldTexts: Record<GenerationRecipeField, string> = {
    name: "Название — от 1 до 120 символов.",
    description: "Описание — до 4000 символов.",
    canvas: "Размер кадра должен совпадать со входом проекта и быть кратен 32.",
    backgroundSetIds: "Выберите хотя бы один набор фонов.",
    classVariantIds: "Нужен хотя бы один вариант с изображениями и долей больше 0%.",
    variantWeights: "Сумма долей вариантов должна быть 100%.",
    objectsPerImage: generationTexts.objectsInvalid,
    emptyProbability: "Доля пустых кадров — от 0 до 100%.",
    scale: generationTexts.scaleInvalid,
};

/** Первое неверное поле формы (правила host) или null: «Сохранить рецепт» и «Пример кадра» доступны только без него. */
export function formFieldError(dto: GenerationRecipeFormDto): GenerationRecipeField | null {
    return recipeFieldError(dto);
}

/** Набор фонов в форме: несовместимый по размеру показан, но недоступен с объяснением (улучшение). */
export function backgroundSetOption(set: BackgroundSetResponseDto, canvas: { width: number; height: number }) {
    const fits = set.targetSize.width === canvas.width && set.targetSize.height === canvas.height;
    return {
        fits,
        reason: fits ? undefined
            : `Размер набора ${set.targetSize.width} × ${set.targetSize.height} не совпадает с кадром ${canvas.width} × ${canvas.height}: такой набор не подойдёт генерации.`,
    };
}

/** «N классов» с русским склонением. */
export function classesLabel(count: number): string {
    const tens = count % 100;
    const units = count % 10;
    const word = tens >= 11 && tens <= 14 ? "классов" : units === 1 ? "класс" : units >= 2 && units <= 4 ? "класса" : "классов";
    return `${count} ${word}`;
}

/** «N фонов» с русским склонением. */
export function backgroundsLabel(count: number): string {
    return `${count.toLocaleString(locale())} ${backgroundsWord(count)}`;
}

/** Ключ настроек формы и seed: пример кадра устарел, если ключ изменился. */
export function sampleKey(dto: GenerationRecipeFormDto, seed: number): string {
    return JSON.stringify([dto, seed]);
}
