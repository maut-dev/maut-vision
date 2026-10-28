import { isRecord } from "../../../transport/envelopes";
import { isCount, isDate, isId } from "../../catalog/catalog-guards";
import type { TrainingParameters } from "../request-dto/training-request-dto";

/** Пресет параметров: общий для всех проектов. */
export interface TrainingPresetResponseDto {
    id: string;
    name: string;
    parameters: TrainingParameters;
    createdAt: string;
    updatedAt: string;
}

export interface TrainingPresetDeletionResponseDto {
    presetId: string;
}

/** Видеокарта по nvidia-smi: device = String(index); память в МиБ. */
export interface GpuResponseDto {
    index: number;
    name: string;
    memoryTotalMb: number;
    driverVersion: string;
}

/** driverAvailable = false — драйвера нет: «Процессор» и ручной ввод устройства. */
export interface GpuInventoryResponseDto {
    driverAvailable: boolean;
    gpus: GpuResponseDto[];
    checkedAt: string;
}

/** Среда Python worker: mode — packaged | interpreter; code / message — почему недоступна (training.runtime_*), null — доступна. */
export interface TrainingRuntimeResponseDto {
    configured: boolean;
    available: boolean;
    mode: "packaged" | "interpreter" | null;
    code: string | null;
    message: string | null;
}

export function isTrainingParameters(value: unknown): value is TrainingParameters {
    return isRecord(value) && Object.values(value).every(item => item === null || ["number", "string", "boolean"].includes(typeof item)
        || (Array.isArray(item) && item.every(entry => typeof entry === "number" || typeof entry === "string")));
}

export function isTrainingPresetResponseDto(value: unknown): value is TrainingPresetResponseDto {
    return isRecord(value) && isId(value.id) && typeof value.name === "string" && isTrainingParameters(value.parameters) && isDate(value.createdAt)
        && isDate(value.updatedAt);
}

export function isTrainingPresetList(value: unknown): value is TrainingPresetResponseDto[] {
    return Array.isArray(value) && value.every(isTrainingPresetResponseDto);
}

export function isTrainingPresetDeletionResponseDto(value: unknown): value is TrainingPresetDeletionResponseDto {
    return isRecord(value) && isId(value.presetId);
}

export function isGpuInventoryResponseDto(value: unknown): value is GpuInventoryResponseDto {
    return isRecord(value) && typeof value.driverAvailable === "boolean" && isDate(value.checkedAt) && Array.isArray(value.gpus)
        && value.gpus.every(gpu => isRecord(gpu) && isCount(gpu.index) && typeof gpu.name === "string" && isCount(gpu.memoryTotalMb)
            && typeof gpu.driverVersion === "string");
}

export function isTrainingRuntimeResponseDto(value: unknown): value is TrainingRuntimeResponseDto {
    return isRecord(value) && typeof value.configured === "boolean" && typeof value.available === "boolean"
        && (value.mode === null || value.mode === "packaged" || value.mode === "interpreter")
        && (value.code === null || typeof value.code === "string") && (value.message === null || typeof value.message === "string");
}
