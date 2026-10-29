import { isCodedTextDto, type CodedTextDto } from "../../coded-text-dto";
import { isRecord } from "../../transport/envelopes";

export type JobStatus = "queued" | "running" | "succeeded" | "failed" | "cancelled" | "interrupted" | "blocked";

/** code — what the UI translates; message — the stored text for the log, shown only for a code the catalogs do not know; may be null. */
export interface JobErrorDto {
    code: string;
    message: string | null;
}

export interface JobResponseDto {
    id: string;
    kind: string;
    projectId: string;
    operationId: string;
    status: JobStatus;
    /** 0–100, or null when the amount of work is unknown. */
    progress: number | null;
    /** What the job is doing: a code of the catalog progress with values ("Epoch 3 of 10"), a stored text of 1.0.0, or null. */
    progressMessage: CodedTextDto | null;
    processed: number | null;
    total: number | null;
    error: JobErrorDto | null;
    cancelRequested: boolean;
    createdAt: string;
    updatedAt: string;
    startedAt: string | null;
    finishedAt: string | null;
    /** Grows with every change: a snapshot with a smaller sequence is stale. */
    sequence: number;
    /**
     * The outcome of the job or null; its shape depends on the job kind. classes.import_assets — ImportClassAssetsResultDto
     * (parseImportClassAssetsResult in api/projects/catalog); images.import — ImportImagesResultDto
     * (parseImportImagesResult in api/projects/images); backgrounds.import — ImportBackgroundsResultDto
     * (parseImportBackgroundsResult in api/projects/backgrounds); generation.run — GenerationRunResultDto
     * (parseGenerationRunResult in api/projects/generation); datasets.build, datasets.verify, datasets.export — DatasetBuildResultDto,
     * DatasetVerifyResultDto, DatasetExportResultDto (parseDataset*Result in api/projects/datasets); training.run —
     * TrainingRunResultDto (parseTrainingRunResult in api/projects/training).
     */
    result: unknown;
    /** What the job works on: a name as text (group, set, preset, dataset) or a code of jobLabels ("Version N"); null without one. */
    label: CodedTextDto | null;
}

const statuses: readonly string[] = ["queued", "running", "succeeded", "failed", "cancelled", "interrupted", "blocked"] satisfies JobStatus[];

const isDate = (value: unknown) => typeof value === "string" && Number.isFinite(Date.parse(value));

const isCounter = (value: unknown) => value === null || (Number.isSafeInteger(value) && (value as number) >= 0);

function isJobErrorDto(value: unknown): value is JobErrorDto {
    return isRecord(value) && typeof value.code === "string" && (value.message === null || typeof value.message === "string");
}

export function isJobResponseDto(value: unknown): value is JobResponseDto {
    return isRecord(value) && typeof value.id === "string" && value.id.length > 0
        && typeof value.kind === "string" && value.kind.length > 0
        && typeof value.projectId === "string" && value.projectId.length > 0
        && typeof value.operationId === "string" && value.operationId.length > 0
        && typeof value.status === "string" && statuses.includes(value.status)
        && (value.progress === null || (typeof value.progress === "number" && value.progress >= 0 && value.progress <= 100))
        && (value.progressMessage === null || isCodedTextDto(value.progressMessage))
        && isCounter(value.processed) && isCounter(value.total)
        && (value.error === null || isJobErrorDto(value.error))
        && typeof value.cancelRequested === "boolean"
        && isDate(value.createdAt) && isDate(value.updatedAt)
        && (value.startedAt === null || isDate(value.startedAt))
        && (value.finishedAt === null || isDate(value.finishedAt))
        && Number.isSafeInteger(value.sequence) && (value.sequence as number) >= 1
        && Object.hasOwn(value, "result")
        && (value.label === null || isCodedTextDto(value.label));
}
