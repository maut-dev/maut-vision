import { isRecord } from "../../transport/envelopes";

/** elapsedMilliseconds — время от старта процесса до первой отметки готовности. */
export interface ReadyResponseDto {
    elapsedMilliseconds: number;
}

export function isReadyResponseDto(value: unknown): value is ReadyResponseDto {
    return isRecord(value) && Number.isFinite(value.elapsedMilliseconds);
}
