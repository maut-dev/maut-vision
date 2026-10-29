import { isJobResponseDto, type JobResponseDto } from "../../../jobs/response-dto/job-response-dto";
import { isRecord } from "../../../transport/envelopes";
import { isCount, isId } from "../../catalog/catalog-guards";
import { isTrainingDatasetDto, type TrainingDatasetDto } from "../../training/response-dto/training-run-response-dto";
import type { EvaluationSplit } from "../request-dto/checks-request-dto";
import { isCodedTextDto, type CodedTextDto } from "../../../coded-text-dto";

/** The job kind of a dataset evaluation: its snapshots come with job.changed. */
export const modelEvaluationJobKind = "checks.evaluate";

/** State of an evaluation; the UI names it by the key checks:evaluationState.<state>. */
export type ModelEvaluationState = "queued" | "preparing" | "running" | "cancelling" | "succeeded" | "failed" | "cancelled" | "interrupted";

/** Metrics of a class (output index): precision, recall, mAP50, mAP50_95; only classes with objects in the split. */
export interface EvaluationClassMetricsDto {
    index: number;
    classId: string | null;
    name: string;
    metrics: Record<string, number>;
}

/**
 * Error of a check: code — what the UI translates; workerMessage — for a failure the worker reported itself (checks.worker_*), a code of the
 * catalog worker with values and its English message as the fallback; null otherwise.
 */
export interface CheckErrorDto {
    code: string;
    workerMessage: CodedTextDto | null;
}

/**
 * An evaluation of a version on a dataset split. job — the snapshot of the checks.evaluate job; metrics — precision, recall, mAP50, mAP50_95 as Ultralytics reports them;
 * imageCount — frames in the split ("dataset · split · frames"); revision — compare with checks.changed and read getEvaluation again.
 */
export interface ModelEvaluationResponseDto {
    id: string;
    job: JobResponseDto;
    state: ModelEvaluationState;
    versionId: string;
    versionNumber: number;
    dataset: TrainingDatasetDto;
    split: EvaluationSplit;
    device: string;
    batch: number;
    imageCount: number | null;
    metrics: Record<string, number>;
    classMetrics: EvaluationClassMetricsDto[];
    environment: Record<string, unknown> | null;
    error: CheckErrorDto | null;
    revision: number;
}

export interface ModelEvaluationPageResponseDto {
    items: ModelEvaluationResponseDto[];
    total: number;
}

const states: readonly string[] = ["queued", "preparing", "running", "cancelling", "succeeded", "failed", "cancelled", "interrupted"] satisfies ModelEvaluationState[];

const isNumbers = (value: unknown) => isRecord(value) && Object.values(value).every(item => typeof item === "number");

export function isCheckErrorDto(value: unknown): value is CheckErrorDto {
    return isRecord(value) && typeof value.code === "string" && value.code.length > 0
        && (value.workerMessage === null || isCodedTextDto(value.workerMessage));
}

export function isModelEvaluationResponseDto(value: unknown): value is ModelEvaluationResponseDto {
    return isRecord(value) && isId(value.id) && isJobResponseDto(value.job) && typeof value.state === "string" && states.includes(value.state)
        && isId(value.versionId) && isCount(value.versionNumber) && isTrainingDatasetDto(value.dataset) && (value.split === "val" || value.split === "test")
        && typeof value.device === "string" && isCount(value.batch) && (value.imageCount === null || isCount(value.imageCount)) && isNumbers(value.metrics)
        && Array.isArray(value.classMetrics) && value.classMetrics.every(item => isRecord(item) && isCount(item.index) && (item.classId === null || isId(item.classId))
            && typeof item.name === "string" && isNumbers(item.metrics))
        && (value.environment === null || isRecord(value.environment)) && (value.error === null || isCheckErrorDto(value.error)) && isCount(value.revision);
}

export function isModelEvaluationPageResponseDto(value: unknown): value is ModelEvaluationPageResponseDto {
    return isRecord(value) && Array.isArray(value.items) && value.items.every(isModelEvaluationResponseDto) && isCount(value.total);
}

export function isModelEvaluationList(value: unknown): value is ModelEvaluationResponseDto[] {
    return Array.isArray(value) && value.every(isModelEvaluationResponseDto);
}
