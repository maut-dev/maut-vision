import { isRecord } from "../../transport/envelopes";

export type BaseModelStatus = "not_installed" | "installing" | "installed" | "failed";

export interface BaseModelErrorDto {
    code: string;
    message: string;
}

export interface BaseModelResponseDto {
    id: string;
    family: string;
    variant: string;
    byteSize: number;
    /** Шаг входа: ширина и высота входа проекта кратны ему. */
    stride: number;
    status: BaseModelStatus;
    /** 0–100 во время установки, иначе 0. */
    progress: number;
    error: BaseModelErrorDto | null;
}

const statuses: readonly string[] = ["not_installed", "installing", "installed", "failed"] satisfies BaseModelStatus[];

function isBaseModelErrorDto(value: unknown): value is BaseModelErrorDto {
    return isRecord(value) && typeof value.code === "string" && typeof value.message === "string";
}

export function isBaseModelResponseDto(value: unknown): value is BaseModelResponseDto {
    return isRecord(value) && typeof value.id === "string" && value.id.length > 0
        && typeof value.family === "string" && typeof value.variant === "string"
        && Number.isSafeInteger(value.byteSize) && (value.byteSize as number) > 0
        && Number.isSafeInteger(value.stride) && (value.stride as number) > 0
        && typeof value.status === "string" && statuses.includes(value.status)
        && typeof value.progress === "number" && value.progress >= 0 && value.progress <= 100
        && (value.error === null || isBaseModelErrorDto(value.error));
}
