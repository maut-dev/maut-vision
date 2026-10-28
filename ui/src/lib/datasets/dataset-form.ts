import type { CatalogWorkspaceResponseDto } from "@/api/projects/catalog/response-dto/catalog-workspace-response-dto";
import {
    datasetTexts,
    datasetVariantRoles,
    defaultDatasetForm,
    randomDatasetSeed,
    ratiosFromPercents,
    type DatasetSpecificationField,
} from "@/api/projects/datasets/dataset-rules";
import type { DatasetSpecificationDto } from "@/api/projects/datasets/request-dto/dataset-request-dto";

/**
 * Значения формы «Подготовить сборку». Проценты (Train 1–99, Test 0–98, Validation — остаток);
 * seed — строка поля, чтобы неверный ввод показывался, а не превращался в число. Роли объектов без варианта и невыбранных
 * классов, фиксированы: «Объект» и «Негатив».
 */
export interface DatasetFormValues {
    name: string;
    groupIds: string[];
    classIds: string[];
    trainPercent: number;
    testPercent: number;
    seed: string;
    includeReviewedEmpty: boolean;
    allowPartialGeneration: boolean;
}

export function emptyDatasetForm(): DatasetFormValues {
    return {
        name: "",
        groupIds: [],
        classIds: [],
        trainPercent: defaultDatasetForm.trainPercent,
        testPercent: defaultDatasetForm.testPercent,
        seed: String(randomDatasetSeed()),
        includeReviewedEmpty: defaultDatasetForm.includeReviewedEmpty,
        allowPartialGeneration: false,
    };
}

/** Целый процент 0–99. */
export function clampPercent(value: number): number {
    return Math.max(0, Math.min(99, Math.round(Number.isFinite(value) ? value : 0)));
}

export function validationPercent(values: Pick<DatasetFormValues, "trainPercent" | "testPercent">): number {
    return 100 - values.trainPercent - values.testPercent;
}

/** Seed поля или NaN, если это не целое число. */
export function parseSeed(text: string): number {
    return /^\s*\d+\s*$/.test(text) ? Number(text) : Number.NaN;
}

/** Спецификация сборки из формы: варианты — по ролям для выбранных классов (datasetVariantRoles). */
export function toSpecification(values: DatasetFormValues, workspace: CatalogWorkspaceResponseDto | undefined): DatasetSpecificationDto {
    const roles = workspace ? datasetVariantRoles(workspace, values.classIds) : { targetVariantIds: [], negativeVariantIds: [] };
    return {
        name: values.name.trim(),
        description: "",
        groupIds: values.groupIds,
        classIds: values.classIds,
        ...roles,
        unassignedVariantRole: "target",
        unselectedClassRole: "negative",
        includeReviewedEmpty: values.includeReviewedEmpty,
        allowPartialGeneration: values.allowPartialGeneration,
        seed: parseSeed(values.seed),
        ...ratiosFromPercents(values.trainPercent, values.testPercent),
    };
}

/** «Настроить повтор»: спецификация сборки обратно в форму (проценты округляются до целых). */
export function formFromSpecification(specification: DatasetSpecificationDto): DatasetFormValues {
    const testPercent = clampPercent(specification.testRatio * 100);
    const trainPercent = Math.max(1, Math.min(99 - testPercent, clampPercent((1 - specification.valRatio - specification.testRatio) * 100)));
    return {
        name: specification.name,
        groupIds: [...specification.groupIds],
        classIds: [...specification.classIds],
        trainPercent,
        testPercent,
        seed: String(specification.seed),
        includeReviewedEmpty: specification.includeReviewedEmpty,
        allowPartialGeneration: specification.allowPartialGeneration,
    };
}

/** Ключ спецификации: план устарел для формы, если ключи различаются. Порядок групп и классов не важен. */
export function specificationKey(specification: DatasetSpecificationDto): string {
    const sorted = (ids: readonly string[]) => [...ids].sort();
    return JSON.stringify({
        ...specification,
        groupIds: sorted(specification.groupIds),
        classIds: sorted(specification.classIds),
        targetVariantIds: sorted(specification.targetVariantIds),
        negativeVariantIds: sorted(specification.negativeVariantIds),
    });
}

/** Подпись неверного поля формы: показывается под полем и в ответ на ошибку host с details.field. */
export const datasetFieldTexts: Record<DatasetSpecificationField, string> = {
    name: "Укажите название датасета до 120 символов.",
    description: "Описание — до 4000 символов.",
    groupIds: "Выберите хотя бы одну группу изображений.",
    classIds: "Выберите хотя бы один класс для обучения.",
    variants: "Проверьте варианты выбранных классов.",
    roles: "Проверьте роли объектов.",
    ratios: datasetTexts.ratiosInvalid,
    seed: datasetTexts.seedInvalid,
};
