import { isRecord } from "../../../transport/envelopes";
import { isCount, isDate, isId } from "../../catalog/catalog-guards";
import type { InputSizeDto } from "../../input-size-dto";
import type { TrainingParameters } from "../request-dto/training-request-dto";
import { isTrainingDatasetDto, type TrainingDatasetDto } from "./training-run-response-dto";
import { isTrainingParameters } from "./training-runtime-response-dto";
import { isCodedTextDto, type CodedTextDto } from "../../../coded-text-dto";

/** A model output class; classId = null for the merged class of singleCls, sourceClassIds — the project classes it stands for. */
export interface ModelClassDto {
    index: number;
    classId: string | null;
    name: string;
    sourceClassIds: string[];
}

/** A version file: kind — weights_pt (best.pt) | onnx (best.onnx) | plot (a chart) | log (the training log); url — for images (the resource channel). */
export interface ModelVersionFileDto {
    kind: "weights_pt" | "onnx" | "plot" | "log";
    name: string;
    byteSize: number;
    sha256: string;
    url: string | null;
}

/**
 * The short passport of a model version: number — "Version N"; runId — "Run passport"; dataset — the dataset and its passport; parameters —
 * requested, effectiveParams — actual; epochsCompleted and stoppedEarly — "early stop"; bestEpoch — the best epoch (by
 * fitness); classes — in the order of model outputs; metrics — the check of best.pt (precision, recall, mAP50, mAP50_95); files with SHA-256;
 * notes — worker warnings (codes worker.* with args; version 1.0.0 stored the text "code: text"); environment — the training environment; isWorking — "Working". The epoch history of the version —
 * trainingApi.listEpochs(runId).
 */
export interface ModelVersionResponseDto {
    id: string;
    number: number;
    runId: string;
    dataset: TrainingDatasetDto;
    architecture: string;
    input: InputSizeDto;
    imageSize: number;
    parameters: TrainingParameters;
    effectiveParams: Record<string, unknown>;
    epochsCompleted: number;
    stoppedEarly: boolean;
    bestEpoch: number | null;
    classes: ModelClassDto[];
    metrics: Record<string, number>;
    files: ModelVersionFileDto[];
    notes: CodedTextDto[];
    environment: Record<string, unknown>;
    isWorking: boolean;
    revision: number;
    createdAt: string;
}

export function isModelVersionResponseDto(value: unknown): value is ModelVersionResponseDto {
    return isRecord(value) && isId(value.id) && isCount(value.number) && isId(value.runId) && isTrainingDatasetDto(value.dataset)
        && typeof value.architecture === "string" && isRecord(value.input) && isCount(value.imageSize) && isTrainingParameters(value.parameters)
        && isRecord(value.effectiveParams) && isCount(value.epochsCompleted) && typeof value.stoppedEarly === "boolean"
        && (value.bestEpoch === null || isCount(value.bestEpoch))
        && Array.isArray(value.classes) && value.classes.every(item => isRecord(item) && isCount(item.index) && (item.classId === null || isId(item.classId))
            && typeof item.name === "string" && Array.isArray(item.sourceClassIds) && item.sourceClassIds.every(isId))
        && isRecord(value.metrics) && Object.values(value.metrics).every(item => typeof item === "number")
        && Array.isArray(value.files) && value.files.every(file => isRecord(file) && ["weights_pt", "onnx", "plot", "log"].includes(file.kind as string)
            && typeof file.name === "string" && isCount(file.byteSize) && typeof file.sha256 === "string" && (file.url === null || typeof file.url === "string"))
        && Array.isArray(value.notes) && value.notes.every(isCodedTextDto) && isRecord(value.environment)
        && typeof value.isWorking === "boolean" && isCount(value.revision) && isDate(value.createdAt);
}

export function isModelVersionList(value: unknown): value is ModelVersionResponseDto[] {
    return Array.isArray(value) && value.every(isModelVersionResponseDto);
}
