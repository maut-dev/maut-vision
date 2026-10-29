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
import { i18n } from "@/i18n";

/**
 * Values of the form "Prepare a build". Percents (Train 1–99, Test 0–98, Validation — the remainder); seed — the text of the field,
 * so a wrong input shows instead of turning into a number. Roles of objects without a variant and of classes not chosen are fixed:
 * "Object" and "Negative".
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

/** A whole percent 0–99. */
export function clampPercent(value: number): number {
    return Math.max(0, Math.min(99, Math.round(Number.isFinite(value) ? value : 0)));
}

export function validationPercent(values: Pick<DatasetFormValues, "trainPercent" | "testPercent">): number {
    return 100 - values.trainPercent - values.testPercent;
}

/** The seed of the field or NaN when it is not an integer. */
export function parseSeed(text: string): number {
    return /^\s*\d+\s*$/.test(text) ? Number(text) : Number.NaN;
}

/** The build specification from the form: variants by roles for the chosen classes (datasetVariantRoles). */
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

/** "Set up a repeat": the build specification back into the form (percents are rounded to whole numbers). */
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

/** The key of a specification: the plan is stale for the form when keys differ. The order of groups and classes does not matter. */
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

/** The text of a wrong form field: shown under the field and for a host error with details.field (read at render time). */
export const datasetFieldTexts: Readonly<Record<DatasetSpecificationField, string>> = {
    get name() { return i18n.t("datasets:fields.name"); },
    get description() { return i18n.t("datasets:fields.description"); },
    get groupIds() { return i18n.t("datasets:fields.groupIds"); },
    get classIds() { return i18n.t("datasets:fields.classIds"); },
    get variants() { return i18n.t("datasets:fields.variants"); },
    get roles() { return i18n.t("datasets:fields.roles"); },
    get ratios() { return datasetTexts.ratiosInvalid; },
    get seed() { return datasetTexts.seedInvalid; },
};
