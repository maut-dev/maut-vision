import { isRecord } from "../../transport/envelopes";

export type JobStatus = "queued" | "running" | "succeeded" | "failed" | "cancelled" | "interrupted" | "blocked";

export interface JobErrorDto {
    code: string;
    message: string;
}

export interface JobResponseDto {
    id: string;
    kind: string;
    projectId: string;
    operationId: string;
    status: JobStatus;
    /** 0–100 или null, если объём работы неизвестен. */
    progress: number | null;
    progressText: string | null;
    processed: number | null;
    total: number | null;
    error: JobErrorDto | null;
    cancelRequested: boolean;
    createdAt: string;
    updatedAt: string;
    startedAt: string | null;
    finishedAt: string | null;
    /** Растёт с каждым изменением: снимок с меньшим sequence устарел. */
    sequence: number;
    /**
     * Итог задачи или null; форму задаёт вид задачи. Для classes.import_assets — ImportClassAssetsResultDto
     * (parseImportClassAssetsResult в api/projects/catalog); для images.import — ImportImagesResultDto
     * (parseImportImagesResult в api/projects/images); для backgrounds.import — ImportBackgroundsResultDto
     * (parseImportBackgroundsResult в api/projects/backgrounds); для generation.run — GenerationRunResultDto
     * (parseGenerationRunResult в api/projects/generation); для datasets.build, datasets.verify, datasets.export — DatasetBuildResultDto,
     * DatasetVerifyResultDto, DatasetExportResultDto (parseDataset*Result в api/projects/datasets); для training.run —
     * TrainingRunResultDto (parseTrainingRunResult в api/projects/training).
     */
    result: unknown;
    /** Подпись постановщика: группа, набор, пресет, датасет, «Версия N»; null у задач без подписи. */
    label: string | null;
}

const statuses: readonly string[] = ["queued", "running", "succeeded", "failed", "cancelled", "interrupted", "blocked"] satisfies JobStatus[];

const isDate = (value: unknown) => typeof value === "string" && Number.isFinite(Date.parse(value));

const isCounter = (value: unknown) => value === null || (Number.isSafeInteger(value) && (value as number) >= 0);

function isJobErrorDto(value: unknown): value is JobErrorDto {
    return isRecord(value) && typeof value.code === "string" && typeof value.message === "string";
}

export function isJobResponseDto(value: unknown): value is JobResponseDto {
    return isRecord(value) && typeof value.id === "string" && value.id.length > 0
        && typeof value.kind === "string" && value.kind.length > 0
        && typeof value.projectId === "string" && value.projectId.length > 0
        && typeof value.operationId === "string" && value.operationId.length > 0
        && typeof value.status === "string" && statuses.includes(value.status)
        && (value.progress === null || (typeof value.progress === "number" && value.progress >= 0 && value.progress <= 100))
        && (value.progressText === null || typeof value.progressText === "string")
        && isCounter(value.processed) && isCounter(value.total)
        && (value.error === null || isJobErrorDto(value.error))
        && typeof value.cancelRequested === "boolean"
        && isDate(value.createdAt) && isDate(value.updatedAt)
        && (value.startedAt === null || isDate(value.startedAt))
        && (value.finishedAt === null || isDate(value.finishedAt))
        && Number.isSafeInteger(value.sequence) && (value.sequence as number) >= 1
        && Object.hasOwn(value, "result")
        && (value.label === null || typeof value.label === "string");
}
