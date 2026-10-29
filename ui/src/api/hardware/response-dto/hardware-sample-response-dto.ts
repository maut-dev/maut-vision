import { isRecord } from "../../transport/envelopes";

export type HardwareStatus = "available" | "collecting" | "unavailable";

/** status collecting — the first load sample; reason — hardware.* code of why there is no value (errorText gives the text). */
export interface CpuSampleDto {
    model: string | null;
    physicalCores: number | null;
    logicalThreads: number;
    utilizationPercent: number | null;
    status: HardwareStatus;
    reason: string | null;
}

export interface MemorySampleDto {
    totalBytes: number;
    usedBytes: number;
    utilizationPercent: number;
}

/** The application: the host, the WebView2 processes and running Python workers with their child processes, in bytes. */
export interface ApplicationMemoryDto {
    totalBytes: number;
    hostBytes: number;
    webViewBytes: number;
    webViewProcesses: number;
    workerBytes: number;
    workerProcesses: number;
}

/** A value the driver does not report is null. */
export interface GpuSampleDto {
    index: number;
    name: string;
    driverVersion: string | null;
    memoryTotalBytes: number | null;
    memoryUsedBytes: number | null;
    utilizationPercent: number | null;
    temperatureC: number | null;
}

/**
 * A sample of the computer on request (hardware.sample). ram is null when Windows did not report it; gpuStatus is available or
 * unavailable with the code gpuReason; gpuSource is nvml or nvidia-smi; gpuSampledAt is the time the GPUs were read.
 */
export interface HardwareSampleResponseDto {
    timestamp: string;
    cpu: CpuSampleDto;
    ram: MemorySampleDto | null;
    application: ApplicationMemoryDto;
    gpus: GpuSampleDto[];
    gpuStatus: HardwareStatus;
    gpuReason: string | null;
    gpuSource: string | null;
    gpuSampledAt: string | null;
}

const statuses: readonly string[] = ["available", "collecting", "unavailable"] satisfies HardwareStatus[];

const isDate = (value: unknown) => typeof value === "string" && Number.isFinite(Date.parse(value));

const isCount = (value: unknown) => Number.isSafeInteger(value) && (value as number) >= 0;

const isNullableCount = (value: unknown) => value === null || isCount(value);

const isPercent = (value: unknown) => typeof value === "number" && value >= 0 && value <= 100;

const isNullableNumber = (value: unknown) => value === null || (typeof value === "number" && Number.isFinite(value));

const isNullableString = (value: unknown) => value === null || typeof value === "string";

function isCpuSampleDto(value: unknown): value is CpuSampleDto {
    return isRecord(value) && isNullableString(value.model) && isNullableCount(value.physicalCores) && isCount(value.logicalThreads)
        && (value.utilizationPercent === null || isPercent(value.utilizationPercent))
        && typeof value.status === "string" && statuses.includes(value.status) && isNullableString(value.reason);
}

function isMemorySampleDto(value: unknown): value is MemorySampleDto {
    return isRecord(value) && isCount(value.totalBytes) && isCount(value.usedBytes) && isPercent(value.utilizationPercent);
}

function isApplicationMemoryDto(value: unknown): value is ApplicationMemoryDto {
    return isRecord(value) && isCount(value.totalBytes) && isCount(value.hostBytes) && isCount(value.webViewBytes) && isCount(value.webViewProcesses)
        && isCount(value.workerBytes) && isCount(value.workerProcesses);
}

function isGpuSampleDto(value: unknown): value is GpuSampleDto {
    return isRecord(value) && isCount(value.index) && typeof value.name === "string" && isNullableString(value.driverVersion)
        && isNullableCount(value.memoryTotalBytes) && isNullableCount(value.memoryUsedBytes)
        && (value.utilizationPercent === null || isPercent(value.utilizationPercent)) && isNullableNumber(value.temperatureC);
}

export function isHardwareSampleResponseDto(value: unknown): value is HardwareSampleResponseDto {
    return isRecord(value) && isDate(value.timestamp) && isCpuSampleDto(value.cpu)
        && (value.ram === null || isMemorySampleDto(value.ram)) && isApplicationMemoryDto(value.application)
        && Array.isArray(value.gpus) && value.gpus.every(isGpuSampleDto)
        && typeof value.gpuStatus === "string" && statuses.includes(value.gpuStatus) && isNullableString(value.gpuReason)
        && isNullableString(value.gpuSource) && (value.gpuSampledAt === null || isDate(value.gpuSampledAt));
}
