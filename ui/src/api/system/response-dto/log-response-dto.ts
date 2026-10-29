import { isRecord } from "../../transport/envelopes";

/** accepted — false when the host dropped the record because of the rate limit. */
export interface LogResponseDto {
    accepted: boolean;
}

export function isLogResponseDto(value: unknown): value is LogResponseDto {
    return isRecord(value) && typeof value.accepted === "boolean";
}
