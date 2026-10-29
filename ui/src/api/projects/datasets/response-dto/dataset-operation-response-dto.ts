import { isJobResponseDto, type JobResponseDto } from "../../../jobs/response-dto/job-response-dto";
import { isRecord } from "../../../transport/envelopes";
import { isCount, isId } from "../../catalog/catalog-guards";
import type { DatasetOperationKind, DatasetSpecificationDto, DatasetSplit } from "../request-dto/dataset-request-dto";
import { isDatasetSpecificationDto, isDatasetSplit, isDatasetSplitCountsDto, type DatasetIntegrityState, type DatasetSplitCountsDto } from "./dataset-response-dto";

/** Job kinds of dataset operations: their snapshots arrive in job.changed. */
export const datasetJobKinds = { build: "datasets.build", verify: "datasets.verify", export: "datasets.export" } as const satisfies Record<DatasetOperationKind, string>;

/**
 * An operation (a card in the tabs "Build", "Checks", "Exports"): job — the job snapshot (status queued / running / succeeded /
 * failed / cancelled / interrupted, cancelRequested, error by code, processed / total — "Images built / Files checked /
 * Images exported: p of t", startedAt / finishedAt — duration, result); versionId and versionName — "Open dataset"
 * with versionAvailable; specification and planId — of a build ("Set up a repeat": a copy of the specification into the form); fileName — of an export.
 * "Cancel" — jobsApi.cancel(projectId, job.id); "Retry" / "Save again" — a new startVerify / selectExportDestination.
 */
export interface DatasetOperationResponseDto {
    job: JobResponseDto;
    kind: DatasetOperationKind;
    versionId: string;
    versionName: string;
    versionAvailable: boolean;
    planId: string | null;
    specification: DatasetSpecificationDto | null;
    fileName: string | null;
}

/** Operations of a kind, newest first; total — "Checks (N)". */
export interface DatasetOperationPageResponseDto {
    items: DatasetOperationResponseDto[];
    total: number;
}

export interface DatasetOperationDeletionResponseDto {
    operationId: string;
}

/** The build result (job.result, a cancelled build has it too): frames total, produced — done; linked / copied / converted — how files were obtained. */
export interface DatasetBuildResultDto {
    versionId: string;
    total: number;
    produced: number;
    linked: number;
    copied: number;
    converted: number;
    counts: DatasetSplitCountsDto;
}

/**
 * An integrity issue: sampleId and split — the frame (null for manifest.json and data.yaml); role — "Dataset description" (manifest, dataYaml),
 * "Image" (image), "Annotation" (label); code — datasets.file_missing | datasets.file_changed | datasets.file_unreadable; the text is errorText(code).
 */
export interface DatasetIntegrityIssueDto {
    sampleId: string | null;
    split: DatasetSplit | null;
    role: "manifest" | "dataYaml" | "image" | "label";
    code: string;
}

/**
 * The result of an integrity check: "Files checked: checkedFiles of totalFiles"; issueCount — "Issues found (n)", issues — the first
 * 500; state — verified | corrupt (null until finished or when cancelled); bytes / elapsedMs — the read speed.
 */
export interface DatasetVerifyResultDto {
    versionId: string;
    totalFiles: number;
    checkedFiles: number;
    issueCount: number;
    issues: DatasetIntegrityIssueDto[];
    state: DatasetIntegrityState | null;
    bytes: number;
    elapsedMs: number;
}

/** The export result: "Images exported: exportedImages of totalImages", fileName, bytes — the ZIP size, elapsedMs. */
export interface DatasetExportResultDto {
    versionId: string;
    fileName: string;
    totalImages: number;
    exportedImages: number;
    bytes: number;
    elapsedMs: number;
}

const kinds: readonly string[] = ["build", "verify", "export"] satisfies DatasetOperationKind[];

export function isDatasetOperationResponseDto(value: unknown): value is DatasetOperationResponseDto {
    return isRecord(value) && isJobResponseDto(value.job) && typeof value.kind === "string" && kinds.includes(value.kind)
        && value.job.kind === datasetJobKinds[value.kind as DatasetOperationKind]
        && isId(value.versionId) && typeof value.versionName === "string" && typeof value.versionAvailable === "boolean"
        && (value.planId === null || isId(value.planId)) && (value.specification === null || isDatasetSpecificationDto(value.specification))
        && (value.fileName === null || typeof value.fileName === "string");
}

export function isDatasetOperationPageResponseDto(value: unknown): value is DatasetOperationPageResponseDto {
    return isRecord(value) && Array.isArray(value.items) && value.items.every(isDatasetOperationResponseDto) && isCount(value.total);
}

export function isDatasetOperationDeletionResponseDto(value: unknown): value is DatasetOperationDeletionResponseDto {
    return isRecord(value) && isId(value.operationId);
}

export function isDatasetBuildResultDto(value: unknown): value is DatasetBuildResultDto {
    return isRecord(value) && isId(value.versionId) && isCount(value.total) && isCount(value.produced) && isCount(value.linked)
        && isCount(value.copied) && isCount(value.converted) && isDatasetSplitCountsDto(value.counts);
}

function isIssue(value: unknown): value is DatasetIntegrityIssueDto {
    return isRecord(value) && (value.sampleId === null || isId(value.sampleId)) && (value.split === null || isDatasetSplit(value.split))
        && ["manifest", "dataYaml", "image", "label"].includes(value.role as string) && typeof value.code === "string";
}

export function isDatasetVerifyResultDto(value: unknown): value is DatasetVerifyResultDto {
    return isRecord(value) && isId(value.versionId) && isCount(value.totalFiles) && isCount(value.checkedFiles) && isCount(value.issueCount)
        && Array.isArray(value.issues) && value.issues.every(isIssue) && (value.state === null || value.state === "verified" || value.state === "corrupt")
        && isCount(value.bytes) && isCount(value.elapsedMs);
}

export function isDatasetExportResultDto(value: unknown): value is DatasetExportResultDto {
    return isRecord(value) && isId(value.versionId) && typeof value.fileName === "string" && isCount(value.totalImages) && isCount(value.exportedImages)
        && isCount(value.bytes) && isCount(value.elapsedMs);
}

/** The build result from a job snapshot; null — a job of another kind or no result yet. */
export function parseDatasetBuildResult(job: JobResponseDto): DatasetBuildResultDto | null {
    return job.kind === datasetJobKinds.build && isDatasetBuildResultDto(job.result) ? job.result : null;
}

/** The integrity check result from a job snapshot; null — a job of another kind or no result yet. */
export function parseDatasetVerifyResult(job: JobResponseDto): DatasetVerifyResultDto | null {
    return job.kind === datasetJobKinds.verify && isDatasetVerifyResultDto(job.result) ? job.result : null;
}

/** The export result from a job snapshot; null — a job of another kind or no result yet. */
export function parseDatasetExportResult(job: JobResponseDto): DatasetExportResultDto | null {
    return job.kind === datasetJobKinds.export && isDatasetExportResultDto(job.result) ? job.result : null;
}
