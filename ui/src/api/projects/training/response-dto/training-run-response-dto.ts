import { isJobResponseDto, type JobResponseDto } from "../../../jobs/response-dto/job-response-dto";
import { isRecord } from "../../../transport/envelopes";
import { isCount, isDate, isId } from "../../catalog/catalog-guards";
import { isDatasetSplitCountsDto, type DatasetClassEntryDto, type DatasetSplitCountsDto } from "../../datasets/response-dto/dataset-response-dto";
import type { InputSizeDto } from "../../input-size-dto";
import type { TrainingParameters } from "../request-dto/training-request-dto";
import { isTrainingParameters } from "./training-runtime-response-dto";
import { isCodedTextDto, type CodedTextDto } from "../../../coded-text-dto";

/** The kind of the training run job: its snapshots arrive in job.changed. */
export const trainingJobKind = "training.run";

/** The run state; labels — trainingRunStateLabels (training-parameters.ts). */
export type TrainingRunState = "queued" | "preparing" | "training" | "cancelling" | "succeeded" | "failed" | "cancelled" | "interrupted";

/** The dataset of the run and the version: the version (passport — datasetsApi.getPassport), the name at the time of the run, classes 0..n−1, frames by part. */
export interface TrainingDatasetDto {
    versionId: string;
    name: string;
    manifestHash: string;
    classes: DatasetClassEntryDto[];
    counts: DatasetSplitCountsDto;
}

/** Progress: stage — the worker stage (trainingStageLabels); "Epoch epoch of epochs", "Batch batch of batches"; metrics — losses and metrics of the last epoch. */
export interface TrainingRunProgressDto {
    stage: string | null;
    epoch: number | null;
    epochs: number | null;
    batch: number | null;
    batches: number | null;
    metrics: Record<string, number>;
}

/**
 * Error of a run: code — what the UI translates; workerMessage — for a failure the worker reported itself (training.worker_*), a code of
 * the catalog worker with values and its English message as the fallback (a run of 1.0.0: the stored text); null otherwise.
 */
export interface TrainingRunErrorDto {
    code: string;
    workerMessage: CodedTextDto | null;
}

/**
 * A training run. job — the snapshot of the job training.run (job.progress 0–100, job.progressMessage progress.epoch, job.cancelRequested); state —
 * one state for labels; parameters — requested (all keys), effectiveParams — actual from the worker (null until they arrive;
 * outputClasses — the output map, lr0Source — request | computed | optimizer-auto); environment — python, torch, ultralytics, cuda,
 * deviceName; epochCount and logCount — for "Epoch history" and "Training log"; resultVersionId — the model version after "Done";
 * revision — compare with training.changed and read getRun again when it is larger.
 */
export interface TrainingRunResponseDto {
    id: string;
    job: JobResponseDto;
    state: TrainingRunState;
    dataset: TrainingDatasetDto;
    architecture: string;
    input: InputSizeDto;
    imageSize: number;
    parameters: TrainingParameters;
    presetName: string | null;
    effectiveParams: Record<string, unknown> | null;
    progress: TrainingRunProgressDto;
    epochCount: number;
    logCount: number;
    environment: Record<string, unknown> | null;
    error: TrainingRunErrorDto | null;
    resultVersionId: string | null;
    revision: number;
}

export interface TrainingRunPageResponseDto {
    items: TrainingRunResponseDto[];
    total: number;
}

/** An epoch: losses — box_loss, cls_loss, dfl_loss; metrics — precision, recall, mAP50, mAP50_95, valBoxLoss, valClsLoss, valDflLoss (empty without a check). */
export interface TrainingEpochDto {
    epoch: number;
    losses: Record<string, number>;
    metrics: Record<string, number>;
    lr: number | null;
    fitness: number | null;
}

/** nextAfter — pass as after for the next page; null — the last one. */
export interface TrainingEpochPageResponseDto {
    items: TrainingEpochDto[];
    total: number;
    nextAfter: number | null;
}

export type TrainingLogKind = "log" | "stage" | "warning" | "system";

/** message — a console line or a stage as text; a system line (trainingLog.*) or a warning (worker.*) as a code with values; lines of 1.0.0 as text. */
export interface TrainingLogEntryDto {
    sequence: number;
    at: string;
    kind: TrainingLogKind;
    message: CodedTextDto;
}

/** total — all entries; nextAfter — the sequence to continue from (for tailLog — of the last entry, to read new ones). */
export interface TrainingLogPageResponseDto {
    items: TrainingLogEntryDto[];
    total: number;
    nextAfter: number | null;
}

/** The result of the job training.run (job.result): the created model version. */
export interface TrainingRunResultDto {
    versionId: string;
}

const states: readonly string[] = ["queued", "preparing", "training", "cancelling", "succeeded", "failed", "cancelled", "interrupted"] satisfies TrainingRunState[];

const isNumbers = (value: unknown): value is Record<string, number> => isRecord(value) && Object.values(value).every(item => typeof item === "number");
const isNullableCount = (value: unknown) => value === null || isCount(value);
const isNullableNumber = (value: unknown) => value === null || typeof value === "number";

export function isTrainingDatasetDto(value: unknown): value is TrainingDatasetDto {
    return isRecord(value) && isId(value.versionId) && typeof value.name === "string" && typeof value.manifestHash === "string"
        && Array.isArray(value.classes) && value.classes.every(item => isRecord(item) && isCount(item.index) && isId(item.classId) && typeof item.name === "string")
        && isDatasetSplitCountsDto(value.counts);
}

export function isTrainingRunResponseDto(value: unknown): value is TrainingRunResponseDto {
    const progress = isRecord(value) ? value.progress : undefined;
    return isRecord(value) && isId(value.id) && isJobResponseDto(value.job) && value.job.kind === trainingJobKind && typeof value.state === "string"
        && states.includes(value.state) && isTrainingDatasetDto(value.dataset) && typeof value.architecture === "string" && isRecord(value.input)
        && isCount(value.imageSize) && isTrainingParameters(value.parameters) && (value.presetName === null || typeof value.presetName === "string")
        && (value.effectiveParams === null || isRecord(value.effectiveParams))
        && isRecord(progress) && (progress.stage === null || typeof progress.stage === "string") && isNullableCount(progress.epoch)
        && isNullableCount(progress.epochs) && isNullableCount(progress.batch) && isNullableCount(progress.batches) && isNumbers(progress.metrics)
        && isCount(value.epochCount) && isCount(value.logCount) && (value.environment === null || isRecord(value.environment))
        && (value.error === null || (isRecord(value.error) && typeof value.error.code === "string"
            && (value.error.workerMessage === null || isCodedTextDto(value.error.workerMessage))))
        && (value.resultVersionId === null || isId(value.resultVersionId)) && isCount(value.revision);
}

export function isTrainingRunPageResponseDto(value: unknown): value is TrainingRunPageResponseDto {
    return isRecord(value) && Array.isArray(value.items) && value.items.every(isTrainingRunResponseDto) && isCount(value.total);
}

function isEpoch(value: unknown): value is TrainingEpochDto {
    return isRecord(value) && isCount(value.epoch) && isNumbers(value.losses) && isNumbers(value.metrics) && isNullableNumber(value.lr)
        && isNullableNumber(value.fitness);
}

export function isTrainingEpochPageResponseDto(value: unknown): value is TrainingEpochPageResponseDto {
    return isRecord(value) && Array.isArray(value.items) && value.items.every(isEpoch) && isCount(value.total) && isNullableCount(value.nextAfter);
}

export function isTrainingLogPageResponseDto(value: unknown): value is TrainingLogPageResponseDto {
    return isRecord(value) && Array.isArray(value.items) && value.items.every(item => isRecord(item) && isCount(item.sequence) && isDate(item.at)
        && ["log", "stage", "warning", "system"].includes(item.kind as string) && isCodedTextDto(item.message))
        && isCount(value.total) && isNullableCount(value.nextAfter);
}

/** The run result from a job snapshot; null — a job of another kind or no result yet. */
export function parseTrainingRunResult(job: JobResponseDto): TrainingRunResultDto | null {
    return job.kind === trainingJobKind && isRecord(job.result) && isId(job.result.versionId) ? { versionId: job.result.versionId } : null;
}
