import { isJobResponseDto, type JobResponseDto } from "../../../jobs/response-dto/job-response-dto";
import { isRecord } from "../../../transport/envelopes";
import { isCount, isDate, isId } from "../../catalog/catalog-guards";
import { isDatasetSplitCountsDto, type DatasetClassEntryDto, type DatasetSplitCountsDto } from "../../datasets/response-dto/dataset-response-dto";
import type { InputSizeDto } from "../../input-size-dto";
import type { TrainingParameters } from "../request-dto/training-request-dto";
import { isTrainingParameters } from "./training-runtime-response-dto";

/** Вид задачи запуска обучения: её снимки приходят в job.changed. */
export const trainingJobKind = "training.run";

/** Состояние запуска; подписи — trainingRunStateLabels (training-parameters.ts). */
export type TrainingRunState = "queued" | "preparing" | "training" | "cancelling" | "succeeded" | "failed" | "cancelled" | "interrupted";

/** Датасет запуска и версии: версия (паспорт — datasetsApi.getPassport), название на момент запуска, классы 0..n−1, кадры по частям. */
export interface TrainingDatasetDto {
    versionId: string;
    name: string;
    manifestHash: string;
    classes: DatasetClassEntryDto[];
    counts: DatasetSplitCountsDto;
}

/** Ход: stage — стадия worker (trainingStageLabels); «Эпоха epoch из epochs», «Пакет batch из batches»; metrics — потери и метрики последней эпохи. */
export interface TrainingRunProgressDto {
    stage: string | null;
    epoch: number | null;
    epochs: number | null;
    batch: number | null;
    batches: number | null;
    metrics: Record<string, number>;
}

/** Error of a run: code — what the UI translates; workerMessage — the worker's own message for its training.worker_* codes, null otherwise. */
export interface TrainingRunErrorDto {
    code: string;
    workerMessage: string | null;
}

/**
 * Запуск обучения. job — снимок задачи training.run (job.progress 0–100, job.progressText «Эпоха X из Y», job.cancelRequested); state —
 * единое состояние для подписей; parameters — запрошенные (все ключи), effectiveParams — фактические от worker (null, пока не пришли;
 * outputClasses — карта выходов, lr0Source — request | computed | optimizer-auto); environment — python, torch, ultralytics, cuda,
 * deviceName; epochCount и logCount — для «История эпох» и «Журнал обучения»; resultVersionId — версия модели после «Готово»;
 * revision — сравнить с training.changed и перечитать getRun, если она больше.
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

/** Эпоха: losses — box_loss, cls_loss, dfl_loss; metrics — precision, recall, mAP50, mAP50_95, valBoxLoss, valClsLoss, valDflLoss (пусто без проверки). */
export interface TrainingEpochDto {
    epoch: number;
    losses: Record<string, number>;
    metrics: Record<string, number>;
    lr: number | null;
    fitness: number | null;
}

/** nextAfter — передать в after для следующей страницы; null — последняя. */
export interface TrainingEpochPageResponseDto {
    items: TrainingEpochDto[];
    total: number;
    nextAfter: number | null;
}

export type TrainingLogKind = "log" | "stage" | "warning" | "system";

export interface TrainingLogEntryDto {
    sequence: number;
    at: string;
    kind: TrainingLogKind;
    message: string;
}

/** total — все записи; nextAfter — sequence для продолжения (у tailLog — последней записи, чтобы дочитывать новые). */
export interface TrainingLogPageResponseDto {
    items: TrainingLogEntryDto[];
    total: number;
    nextAfter: number | null;
}

/** Итог задачи training.run (job.result): созданная версия модели. */
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
            && (value.error.workerMessage === null || typeof value.error.workerMessage === "string")))
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
        && ["log", "stage", "warning", "system"].includes(item.kind as string) && typeof item.message === "string")
        && isCount(value.total) && isNullableCount(value.nextAfter);
}

/** Итог запуска из снимка задачи; null — задача другого вида или итога ещё нет. */
export function parseTrainingRunResult(job: JobResponseDto): TrainingRunResultDto | null {
    return job.kind === trainingJobKind && isRecord(job.result) && isId(job.result.versionId) ? { versionId: job.result.versionId } : null;
}
