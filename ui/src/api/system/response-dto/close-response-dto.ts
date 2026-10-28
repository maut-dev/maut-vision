import { isRecord } from "../../transport/envelopes";

/** accepted — false, если ответ относится к уже неактуальному запросу закрытия. */
export interface CloseResponseDto {
    accepted: boolean;
}

export function isCloseResponseDto(value: unknown): value is CloseResponseDto {
    return isRecord(value) && typeof value.accepted === "boolean";
}
