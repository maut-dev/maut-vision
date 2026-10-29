import { i18n } from "@/i18n";
import "@/i18n/sections/datasets";
import type { CatalogWorkspaceResponseDto } from "../catalog/response-dto/catalog-workspace-response-dto";
import type { DatasetSpecificationDto } from "./request-dto/dataset-request-dto";

/** The same limits the host checks (DatasetSelection.Validate). */
export const maxDatasetNameLength = 120;
export const maxDatasetDescriptionLength = 4000;
export const maxDatasetSeed = 4_294_967_295;

/** Initial values of the form: Train 80 %, Test 0 %, empty frames included, a random seed. */
export const defaultDatasetForm = { trainPercent: 80, testPercent: 0, includeReviewedEmpty: true } as const;

/** A form field; matches details.field of the errors datasets.*_invalid. */
export type DatasetSpecificationField = "name" | "description" | "groupIds" | "classIds" | "variants" | "roles" | "ratios" | "seed";

/** Hints of the form and empty states in the current language (read at render time). */
export const datasetTexts = {
    validationRemainder: (percent: number) => i18n.t("datasets:rules.validationRemainder", { percent }),
    get ratiosInvalid() { return i18n.t("datasets:rules.ratiosInvalid"); },
    get seedHint() { return i18n.t("datasets:rules.seedHint"); },
    get seedInvalid() { return i18n.t("datasets:rules.seedInvalid"); },
    get emptyList() { return i18n.t("datasets:rules.emptyList"); },
    get loading() { return i18n.t("datasets:rules.loading"); },
    get sizeMismatch() { return i18n.t("datasets:rules.sizeMismatch"); },
    get archived() { return i18n.t("datasets:rules.archived"); },
    get integrityHelp() { return i18n.t("datasets:rules.integrityHelp"); },
};

/** A split label: train → Train, val → Validation, test → Test (the names of the splits, the same in every language). */
export const datasetSplitLabels = { train: "Train", val: "Validation", test: "Test" } as const;

/** "Integrity: verified / corrupt / not verified" (read at render time). */
export const integrityLabels = {
    get verified() { return i18n.t("datasets:integrity.verified"); },
    get corrupt() { return i18n.t("datasets:integrity.corrupt"); },
    get unverified() { return i18n.t("datasets:integrity.unverified"); },
};

/** Box colors of the viewer: objects, negatives (dashed), ROI. */
export const datasetOverlayColors = { box: "#5fb3ff", negative: "#f6b44c", roi: "#69d3a2" } as const;

/** "New random seed": an integer 0–4 294 967 295 from the cryptographic generator. */
export function randomDatasetSeed(): number {
    const value = new Uint32Array(1);
    crypto.getRandomValues(value);
    return value[0];
}

/** Ratios of the specification from the form percents: validation is the remainder after Train and Test. */
export function ratiosFromPercents(trainPercent: number, testPercent: number): { valRatio: number; testRatio: number } {
    const val = 100 - trainPercent - testPercent;
    return { valRatio: val / 100, testRatio: testPercent / 100 };
}

/**
 * Variant roles: targets — available variants of the chosen classes, negatives — unavailable ones (archived themselves, through the
 * class or the project variant). The host turns objects of deleted and archived variants into negatives anyway.
 */
export function datasetVariantRoles(workspace: CatalogWorkspaceResponseDto, classIds: readonly string[]): { targetVariantIds: string[]; negativeVariantIds: string[] } {
    const selected = new Set(classIds);
    const bindings = workspace.classVariants.filter(binding => selected.has(binding.classId));
    return {
        targetVariantIds: bindings.filter(binding => binding.available).map(binding => binding.id),
        negativeVariantIds: bindings.filter(binding => !binding.available).map(binding => binding.id),
    };
}

/** The first wrong field of the specification or null — the same rules as the host. */
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
