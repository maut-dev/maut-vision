import type { JobErrorDto, JobResponseDto } from "../../jobs/response-dto/job-response-dto";
import { isCodedTextDto, type CodedTextDto } from "../../coded-text-dto";
import { isRecord } from "../../transport/envelopes";

/** The job kind of a project export. */
export const projectExportJobKind = "projects.export";

/** Where the export is saved; null when the window was closed without a choice. The path does not reach the UI. */
export interface ProjectExportSelectionResponseDto {
    selectionToken: string | null;
    fileName: string | null;
}

/** The chosen archive; null when the window was closed without a choice. */
export interface ProjectArchiveSelectionResponseDto {
    selectionToken: string | null;
    fileName: string | null;
    byteSize: number | null;
}

export type ProjectImportStatus = "running" | "succeeded" | "failed" | "cancelled";

/**
 * A project import: counters of files and unpacked bytes (total is 0 until the archive is checked); on success projectId and projectName —
 * the added project (a new id when the id matches, the copy suffix when the name matches); error — the code of the refusal.
 * Apply a snapshot only when sequence is greater than the known one. The banner reads "Importing a project…", "Files: N of M.".
 */
export interface ProjectImportResponseDto {
    id: string;
    operationId: string;
    fileName: string;
    status: ProjectImportStatus;
    cancelRequested: boolean;
    processedFiles: number;
    totalFiles: number;
    processedBytes: number;
    totalBytes: number;
    projectId: string | null;
    projectName: string | null;
    error: JobErrorDto | null;
    startedAt: string;
    finishedAt: string | null;
    sequence: number;
}

/** Running work of a project: a job (kind — its kind, jobId, label) or a live check (kind "checks.live", jobId null). */
export interface ProjectActivityResponseDto {
    kind: string;
    jobId: string | null;
    /** The label of the job: a name as text or a code of jobLabels (lib/coded-text.ts). */
    label: CodedTextDto | null;
}

/** activities — what the deletion cancels (Cancel jobs and delete); transferActive — an export runs, the deletion is refused. */
export interface ProjectDeletionReviewResponseDto {
    projectId: string;
    name: string;
    revision: number;
    activities: ProjectActivityResponseDto[];
    transferActive: boolean;
}

/** cleanupPending — the project is deleted, but some files are still busy; the application repeats the cleanup at the next start. */
export interface ProjectDeletionResponseDto {
    projectId: string;
    cleanupPending: boolean;
}

/** The outcome of projects.export (also after a cancel or an error — how much was done); links — paths of hard links without repeated content. */
export interface ProjectExportResultDto {
    fileName: string;
    files: number;
    totalFiles: number;
    bytes: number;
    totalBytes: number;
    links: number;
    archiveBytes: number;
    elapsedMs: number;
}

const isDate = (value: unknown) => typeof value === "string" && Number.isFinite(Date.parse(value));
const isCount = (value: unknown) => Number.isSafeInteger(value) && (value as number) >= 0;
const isText = (value: unknown) => typeof value === "string" && value.length > 0;
const isOptionalText = (value: unknown) => value === null || isText(value);
const importStatuses: readonly string[] = ["running", "succeeded", "failed", "cancelled"] satisfies ProjectImportStatus[];

export function isProjectExportSelectionResponseDto(value: unknown): value is ProjectExportSelectionResponseDto {
    return isRecord(value) && isOptionalText(value.selectionToken) && isOptionalText(value.fileName);
}

export function isProjectArchiveSelectionResponseDto(value: unknown): value is ProjectArchiveSelectionResponseDto {
    return isRecord(value) && isOptionalText(value.selectionToken) && isOptionalText(value.fileName) && (value.byteSize === null || isCount(value.byteSize));
}

export function isProjectImportResponseDto(value: unknown): value is ProjectImportResponseDto {
    return isRecord(value) && isText(value.id) && isText(value.operationId) && typeof value.fileName === "string"
        && typeof value.status === "string" && importStatuses.includes(value.status) && typeof value.cancelRequested === "boolean"
        && isCount(value.processedFiles) && isCount(value.totalFiles) && isCount(value.processedBytes) && isCount(value.totalBytes)
        && isOptionalText(value.projectId) && isOptionalText(value.projectName)
        && (value.error === null || (isRecord(value.error) && typeof value.error.code === "string"
            && (value.error.message === null || typeof value.error.message === "string")))
        && isDate(value.startedAt) && (value.finishedAt === null || isDate(value.finishedAt)) && isCount(value.sequence);
}

export function isProjectImportListResponseDto(value: unknown): value is ProjectImportResponseDto[] {
    return Array.isArray(value) && value.every(isProjectImportResponseDto);
}

function isProjectActivityResponseDto(value: unknown): value is ProjectActivityResponseDto {
    return isRecord(value) && isText(value.kind) && isOptionalText(value.jobId) && (value.label === null || isCodedTextDto(value.label));
}

export function isProjectDeletionReviewResponseDto(value: unknown): value is ProjectDeletionReviewResponseDto {
    return isRecord(value) && isText(value.projectId) && typeof value.name === "string" && Number.isSafeInteger(value.revision)
        && Array.isArray(value.activities) && value.activities.every(isProjectActivityResponseDto) && typeof value.transferActive === "boolean";
}

export function isProjectDeletionResponseDto(value: unknown): value is ProjectDeletionResponseDto {
    return isRecord(value) && isText(value.projectId) && typeof value.cleanupPending === "boolean";
}

function isProjectExportResultDto(value: unknown): value is ProjectExportResultDto {
    return isRecord(value) && typeof value.fileName === "string" && isCount(value.files) && isCount(value.totalFiles) && isCount(value.bytes)
        && isCount(value.totalBytes) && isCount(value.links) && isCount(value.archiveBytes) && isCount(value.elapsedMs);
}

/** The outcome of a projects.export job, or null when it is another job or there is no outcome yet. */
export function parseProjectExportResult(job: JobResponseDto): ProjectExportResultDto | null {
    return job.kind === projectExportJobKind && isProjectExportResultDto(job.result) ? job.result : null;
}
