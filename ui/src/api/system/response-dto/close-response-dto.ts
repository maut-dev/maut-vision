import { isRecord } from "../../transport/envelopes";

/** accepted — false when the answer belongs to a close request that is no longer current. */
export interface CloseResponseDto {
    accepted: boolean;
}

export function isCloseResponseDto(value: unknown): value is CloseResponseDto {
    return isRecord(value) && typeof value.accepted === "boolean";
}
