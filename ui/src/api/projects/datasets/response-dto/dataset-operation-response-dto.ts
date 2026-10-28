import { isJobResponseDto, type JobResponseDto } from "../../../jobs/response-dto/job-response-dto";
import { isRecord } from "../../../transport/envelopes";
import { isCount, isId } from "../../catalog/catalog-guards";
import type { DatasetOperationKind, DatasetSpecificationDto, DatasetSplit } from "../request-dto/dataset-request-dto";
import { isDatasetSpecificationDto, isDatasetSplit, isDatasetSplitCountsDto, type DatasetIntegrityState, type DatasetSplitCountsDto } from "./dataset-response-dto";

/** Виды задач операций датасетов: их снимки приходят в job.changed. */
export const datasetJobKinds = { build: "datasets.build", verify: "datasets.verify", export: "datasets.export" } as const satisfies Record<DatasetOperationKind, string>;

/**
 * Операция (карточка во вкладках «Сборка», «Проверки», «Экспорты»): job — снимок задачи (status queued / running / succeeded /
 * failed / cancelled / interrupted, cancelRequested, error с русским текстом, processed / total — «Собрано / Проверено файлов /
 * Экспортировано изображений: p из t», startedAt / finishedAt — длительность, result); versionId и versionName — «Открыть датасет»
 * при versionAvailable; specification и planId — у сборки («Настроить повтор»: копия спецификации в форму); fileName — у экспорта.
 * «Отменить» — jobsApi.cancel(projectId, job.id); «Повторить» / «Сохранить ещё раз» — новый startVerify / selectExportDestination.
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

/** Операции вида, новые первыми; total — «Проверки (N)». */
export interface DatasetOperationPageResponseDto {
    items: DatasetOperationResponseDto[];
    total: number;
}

export interface DatasetOperationDeletionResponseDto {
    operationId: string;
}

/** Итог сборки (job.result, есть и у отменённой): кадров total, produced — готово; linked / copied / converted — как получены файлы. */
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
 * Проблема целостности: sampleId и split — кадр (null у manifest.json и data.yaml); role — «Описание датасета» (manifest, dataYaml),
 * «Изображение» (image), «Разметка» (label); code — datasets.file_missing | datasets.file_changed | datasets.file_unreadable; the text is errorText(code).
 */
export interface DatasetIntegrityIssueDto {
    sampleId: string | null;
    split: DatasetSplit | null;
    role: "manifest" | "dataYaml" | "image" | "label";
    code: string;
}

/**
 * Итог проверки целостности: «Проверено файлов: checkedFiles из totalFiles»; issueCount — «Обнаруженные проблемы (n)», issues — первые
 * 500; state — verified | corrupt (null, пока не закончена или если отменена); bytes / elapsedMs — скорость чтения.
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

/** Итог экспорта: «Экспортировано изображений: exportedImages из totalImages», fileName, bytes — размер ZIP, elapsedMs. */
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

/** Итог сборки из снимка задачи; null — задача другого вида или итога ещё нет. */
export function parseDatasetBuildResult(job: JobResponseDto): DatasetBuildResultDto | null {
    return job.kind === datasetJobKinds.build && isDatasetBuildResultDto(job.result) ? job.result : null;
}

/** Итог проверки целостности из снимка задачи; null — задача другого вида или итога ещё нет. */
export function parseDatasetVerifyResult(job: JobResponseDto): DatasetVerifyResultDto | null {
    return job.kind === datasetJobKinds.verify && isDatasetVerifyResultDto(job.result) ? job.result : null;
}

/** Итог экспорта из снимка задачи; null — задача другого вида или итога ещё нет. */
export function parseDatasetExportResult(job: JobResponseDto): DatasetExportResultDto | null {
    return job.kind === datasetJobKinds.export && isDatasetExportResultDto(job.result) ? job.result : null;
}
