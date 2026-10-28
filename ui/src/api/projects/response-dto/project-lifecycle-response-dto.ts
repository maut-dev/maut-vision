import type { JobErrorDto, JobResponseDto } from "../../jobs/response-dto/job-response-dto";
import { isRecord } from "../../transport/envelopes";

/** Вид задачи экспорта проекта. */
export const projectExportJobKind = "projects.export";

/** Место сохранения экспорта; null — окно закрыто без выбора. Путь в UI не приходит. */
export interface ProjectExportSelectionResponseDto {
    selectionToken: string | null;
    fileName: string | null;
}

/** Выбранный архив; null — окно закрыто без выбора. */
export interface ProjectArchiveSelectionResponseDto {
    selectionToken: string | null;
    fileName: string | null;
    byteSize: number | null;
}

export type ProjectImportStatus = "running" | "succeeded" | "failed" | "cancelled";

/**
 * Импорт проекта: счётчики файлов и байтов распаковки (total — 0 до проверки архива); при успехе projectId и projectName —
 * добавленный проект (при совпадении id — новый, при совпадении имени — с « (импорт)»); error — код и текст отказа.
 * Применять снимок, только если sequence больше известного. Тексты: «Импорт проекта…», «Файлов: N из M.».
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

/** Идущая работа проекта: задача (kind — её вид, jobId, label) или живая проверка (kind "checks.live", jobId null). */
export interface ProjectActivityResponseDto {
    kind: string;
    jobId: string | null;
    label: string | null;
}

/** activities — что удаление отменит («Отменить задачи и удалить»); transferActive — идёт экспорт, удаление откажет. */
export interface ProjectDeletionReviewResponseDto {
    projectId: string;
    name: string;
    revision: number;
    activities: ProjectActivityResponseDto[];
    transferActive: boolean;
}

/** cleanupPending — «Проект удалён. Часть файлов пока занята; приложение повторит очистку при следующем запуске.» */
export interface ProjectDeletionResponseDto {
    projectId: string;
    cleanupPending: boolean;
}

/** Итог projects.export (и после отмены или ошибки — сколько успели); links — пути жёстких ссылок без повторного содержимого. */
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
        && (value.error === null || (isRecord(value.error) && typeof value.error.code === "string" && typeof value.error.message === "string"))
        && isDate(value.startedAt) && (value.finishedAt === null || isDate(value.finishedAt)) && isCount(value.sequence);
}

export function isProjectImportListResponseDto(value: unknown): value is ProjectImportResponseDto[] {
    return Array.isArray(value) && value.every(isProjectImportResponseDto);
}

function isProjectActivityResponseDto(value: unknown): value is ProjectActivityResponseDto {
    return isRecord(value) && isText(value.kind) && isOptionalText(value.jobId) && (value.label === null || typeof value.label === "string");
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

/** Итог задачи projects.export или null, если это другая задача или итога ещё нет. */
export function parseProjectExportResult(job: JobResponseDto): ProjectExportResultDto | null {
    return job.kind === projectExportJobKind && isProjectExportResultDto(job.result) ? job.result : null;
}
