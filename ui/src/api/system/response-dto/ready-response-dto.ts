import { isRecord } from "../../transport/envelopes";

/** elapsedMilliseconds — the time from the process start to the first readiness mark. */
export interface ReadyResponseDto {
    elapsedMilliseconds: number;
}

export function isReadyResponseDto(value: unknown): value is ReadyResponseDto {
    return isRecord(value) && Number.isFinite(value.elapsedMilliseconds);
}
