import type { JobResponseDto } from "@/api/jobs/response-dto/job-response-dto";
import { datasetOverlayColors, datasetSplitLabels } from "@/api/projects/datasets/dataset-rules";
import type { DatasetSplit } from "@/api/projects/datasets/request-dto/dataset-request-dto";
import {
    parseDatasetBuildResult,
    parseDatasetExportResult,
    parseDatasetVerifyResult,
    type DatasetIntegrityIssueDto,
    type DatasetOperationResponseDto,
} from "@/api/projects/datasets/response-dto/dataset-operation-response-dto";
import type {
    DatasetPlanSkipsDto,
    DatasetSampleResponseDto,
    DatasetVersionResponseDto,
} from "@/api/projects/datasets/response-dto/dataset-response-dto";
import type { Box } from "@/lib/annotation/geometry";
import { i18n } from "@/i18n";
import { jobErrorText } from "@/lib/errors";
import { formatDuration, formatNumber, locale } from "@/lib/format";
import "@/i18n/sections/datasets";

export const datasetSplits: readonly DatasetSplit[] = ["train", "val", "test"];

export const count = (value: number) => value.toLocaleString(locale());

/** Search in the dataset list: the name and the description ignoring case. */
export function matchesSearch(version: DatasetVersionResponseDto, query: string): boolean {
    const needle = query.trim().toLocaleLowerCase(locale());
    return !needle || version.name.toLocaleLowerCase(locale()).includes(needle) || version.description.toLocaleLowerCase(locale()).includes(needle);
}

/** Skip reasons of a plan with non-zero counters: "not reviewed: 3" and so on. */
export function skipReasons(skipped: DatasetPlanSkipsDto): { key: keyof DatasetPlanSkipsDto; label: string; count: number }[] {
    const keys = ["unreviewed", "partialGeneration", "withoutTargets", "invalidGeometry"] as const;
    return keys.map(key => ({ key, label: i18n.t(`datasets:skipReasons.${key}`), count: skipped[key] })).filter(reason => reason.count > 0);
}

/** "0: A, 1: B". */
export function classOrderText(version: Pick<DatasetVersionResponseDto, "classMap">): string {
    return [...version.classMap].sort((left, right) => left.index - right.index).map(entry => `${entry.index}: ${entry.name}`).join(", ");
}

export function classLabel(version: DatasetVersionResponseDto, classId: string, index?: number): string {
    const entry = version.classMap.find(item => item.classId === classId && (index === undefined || item.index === index));
    return entry ? `${entry.index}. ${entry.name}` : index === undefined ? i18n.t("datasets:view.classOutside") : i18n.t("datasets:view.classIndex", { index });
}

/** The variant name at build time or null (an object without a variant). */
export function variantName(version: DatasetVersionResponseDto, classVariantId: string | null): string | null {
    if (!classVariantId) return null;
    return version.variants.find(item => item.classVariantId === classVariantId)?.name ?? i18n.t("datasets:view.variantDeleted");
}

/** YOLO center and size → the top left corner. */
export function centerBox(box: { centerX: number; centerY: number; width: number; height: number }): Box {
    return { x: box.centerX - box.width / 2, y: box.centerY - box.height / 2, width: box.width, height: box.height };
}

export type SampleOverlayKind = "object" | "negative" | "roi";

export interface SampleOverlay {
    id: string;
    kind: SampleOverlayKind;
    label: string;
    color: string;
    box: Box;
}

/** Boxes of a sample: objects #5fb3ff, negatives #f6b44c (dashed), ROI #69d3a2; labels — the class and the variant. */
export function sampleOverlays(sample: DatasetSampleResponseDto, version: DatasetVersionResponseDto): SampleOverlay[] {
    const withVariant = (label: string, id: string | null) => {
        const name = variantName(version, id);
        return name ? `${label} · ${name}` : label;
    };
    const result: SampleOverlay[] = sample.boxes.map((box, ordinal) => ({
        id: `object-${ordinal}`, kind: "object", color: datasetOverlayColors.box,
        label: withVariant(classLabel(version, box.classId, box.index), box.classVariantId), box: centerBox(box),
    }));
    result.push(...sample.negatives.map((box, ordinal): SampleOverlay => ({
        id: `negative-${ordinal}`, kind: "negative", color: datasetOverlayColors.negative,
        label: withVariant(i18n.t("datasets:view.negative", { label: classLabel(version, box.classId) }), box.classVariantId), box: centerBox(box),
    })));
    if (sample.roi) result.push({ id: "roi", kind: "roi", color: datasetOverlayColors.roi, label: i18n.t("datasets:view.roi"), box: { ...sample.roi } });
    return result;
}

export function boxPixels(box: Box, width: number, height: number): string {
    const number = (value: number) => formatNumber(Number(value.toFixed(2)));
    return i18n.t("datasets:view.boxPixels", { x: number(box.x * width), y: number(box.y * height), width: number(box.width * width), height: number(box.height * height) });
}

export const splitLabel = (split: DatasetSplit) => datasetSplitLabels[split];

/** The operation is still going: it can be cancelled but not deleted from the history. */
export function isOperationActive(job: JobResponseDto): boolean {
    return job.status === "queued" || job.status === "running" || job.status === "blocked";
}

export function duration(job: JobResponseDto): string | null {
    if (!job.startedAt || !job.finishedAt) return null;
    const milliseconds = Date.parse(job.finishedAt) - Date.parse(job.startedAt);
    return Number.isFinite(milliseconds) ? formatDuration(milliseconds) : null;
}

/** Titles of operations in the current language (read at render time). */
export const operationTitles = {
    get build() { return i18n.t("datasets:operations.build"); },
    get verify() { return i18n.t("datasets:operations.verify"); },
    get export() { return i18n.t("datasets:operations.export"); },
};

/**
 * Progress of an operation by the last job snapshot: the job result wins over processed/total. The label — "Images built / Files
 * checked / Images exported"; the line is "label: p of t".
 */
export function operationProgress(operation: DatasetOperationResponseDto): { label: string; processed: number; total: number | null; percent: number | null } {
    const { job } = operation;
    let processed = job.processed ?? 0;
    let total = job.total;
    let label = i18n.t("datasets:progress.build");
    if (operation.kind === "build") {
        const result = parseDatasetBuildResult(job);
        if (result) { processed = result.produced; total = result.total; }
    } else if (operation.kind === "verify") {
        label = i18n.t("datasets:progress.verify");
        const result = parseDatasetVerifyResult(job);
        if (result) { processed = result.checkedFiles; total = result.totalFiles; }
    } else {
        label = i18n.t("datasets:progress.export");
        const result = parseDatasetExportResult(job);
        if (result) { processed = result.exportedImages; total = result.totalImages; }
    }
    const percent = total ? Math.min(100, Math.floor(100 * processed / total)) : job.status === "succeeded" ? 100 : job.progress;
    return { label, processed, total, percent };
}

/** Labels of file roles of integrity issues (read at render time). */
export const issueRoleLabels: Readonly<Record<DatasetIntegrityIssueDto["role"], string>> = {
    get manifest() { return i18n.t("datasets:issueRoles.manifest"); },
    get dataYaml() { return i18n.t("datasets:issueRoles.manifest"); },
    get image() { return i18n.t("datasets:issueRoles.image"); },
    get label() { return i18n.t("datasets:issueRoles.label"); },
};

/** The state text of an operation that ended without a host error (cancelled, interrupted). */
export function operationEndText(operation: DatasetOperationResponseDto): string | null {
    const { job } = operation;
    if (isOperationActive(job) || job.status === "succeeded") return null;
    if (job.error) return jobErrorText(job.error);
    if (job.status === "cancelled") return operation.kind === "build" ? i18n.t("datasets:operationEnd.buildCancelled") : i18n.t("datasets:operationEnd.cancelled");
    if (job.status === "interrupted") return i18n.t("datasets:operationEnd.interrupted");
    return null;
}
