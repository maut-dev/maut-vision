import type { CatalogWorkspaceResponseDto } from "../catalog/response-dto/catalog-workspace-response-dto";
import type { DatasetSpecificationDto } from "./request-dto/dataset-request-dto";

/** Те же пределы, что проверяет host (DatasetSelection.Validate). */
export const maxDatasetNameLength = 120;
export const maxDatasetDescriptionLength = 4000;
export const maxDatasetSeed = 4_294_967_295;

/** Начальные значения формы: Train 80 %, Test 0 %, пустые кадры включены, seed случайный. */
export const defaultDatasetForm = { trainPercent: 80, testPercent: 0, includeReviewedEmpty: true } as const;

/** Поле формы; совпадает с details.field ошибок datasets.*_invalid. */
export type DatasetSpecificationField = "name" | "description" | "groupIds" | "classIds" | "variants" | "roles" | "ratios" | "seed";

/** Тексты подсказок формы и пустых состояний. */
export const datasetTexts = {
    validationRemainder: (percent: number) => `Для проверки (Validation): ${percent}% — остаток после Train и Test.`,
    ratiosInvalid: "Сумма Train и Test должна быть меньше 100%: остаток уходит в проверку (Validation).",
    seedHint: "Выбирается случайно при открытии. Тот же seed на тех же источниках даёт то же разбиение.",
    seedInvalid: "Seed — целое число от 0 до 4 294 967 295.",
    emptyList: "Соберите датасет во вкладке «Сборка». Исходные изображения сохранятся.",
    loading: "Загружаем датасеты…",
    sizeMismatch: "Размер отличается от входа проекта",
    archived: "В архиве",
    integrityHelp: "Соберите новую версию из сохранённых исходников. Исправление исходного изображения не меняет эту копию.",
} as const;

/** Подпись части: train → Train, val → Validation, test → Test. */
export const datasetSplitLabels = { train: "Train", val: "Validation", test: "Test" } as const;

/** «Целостность: проверена / нарушена / не проверена». */
export const integrityLabels = { verified: "проверена", corrupt: "нарушена", unverified: "не проверена" } as const;

/** Цвета рамок просмотрщика: объекты, негативы (пунктиром), ROI. */
export const datasetOverlayColors = { box: "#5fb3ff", negative: "#f6b44c", roi: "#69d3a2" } as const;

/** «Новый случайный seed»: целое 0–4 294 967 295 из криптографического генератора. */
export function randomDatasetSeed(): number {
    const value = new Uint32Array(1);
    crypto.getRandomValues(value);
    return value[0];
}

/** Доли спецификации из процентов формы: validation — остаток после Train и Test. */
export function ratiosFromPercents(trainPercent: number, testPercent: number): { valRatio: number; testRatio: number } {
    const val = 100 - trainPercent - testPercent;
    return { valRatio: val / 100, testRatio: testPercent / 100 };
}

/**
 * Роли вариантов: целевые — доступные варианты выбранных классов, негативные — недоступные (архивные сами,
 * через класс или вариант проекта). Объекты удалённых и архивных вариантов host и так сделает негативами.
 */
export function datasetVariantRoles(workspace: CatalogWorkspaceResponseDto, classIds: readonly string[]): { targetVariantIds: string[]; negativeVariantIds: string[] } {
    const selected = new Set(classIds);
    const bindings = workspace.classVariants.filter(binding => selected.has(binding.classId));
    return {
        targetVariantIds: bindings.filter(binding => binding.available).map(binding => binding.id),
        negativeVariantIds: bindings.filter(binding => !binding.available).map(binding => binding.id),
    };
}

/** Первое неверное поле спецификации или null — те же правила, что у host. */
export function specificationFieldError(specification: DatasetSpecificationDto): DatasetSpecificationField | null {
    const name = specification.name.trim();
    if (name.length === 0 || name.length > maxDatasetNameLength) return "name";
    if (specification.description.trim().length > maxDatasetDescriptionLength) return "description";
    const distinct = (ids: readonly string[], required: boolean) => (!required || ids.length > 0) && new Set(ids).size === ids.length;
    if (!distinct(specification.groupIds, true)) return "groupIds";
    if (!distinct(specification.classIds, true)) return "classIds";
    if (!distinct(specification.targetVariantIds, false) || !distinct(specification.negativeVariantIds, false)
        || specification.targetVariantIds.some(id => specification.negativeVariantIds.includes(id))) return "variants";
    const { valRatio, testRatio } = specification;
    if (!Number.isFinite(valRatio) || !Number.isFinite(testRatio) || valRatio <= 0 || testRatio < 0 || valRatio + testRatio >= 1) return "ratios";
    if (!Number.isSafeInteger(specification.seed) || specification.seed < 0 || specification.seed > maxDatasetSeed) return "seed";
    return null;
}
