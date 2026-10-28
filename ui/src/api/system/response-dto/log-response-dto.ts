import { isRecord } from "../../transport/envelopes";

/** accepted — false, если host отбросил запись из-за ограничения частоты. */
export interface LogResponseDto {
    accepted: boolean;
}

export function isLogResponseDto(value: unknown): value is LogResponseDto {
    return isRecord(value) && typeof value.accepted === "boolean";
}
