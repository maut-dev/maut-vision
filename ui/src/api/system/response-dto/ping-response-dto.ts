import { isRecord } from "../../transport/envelopes";

export interface PingResponseDto {
    message: string;
    host: string;
    utc: string;
}

export function isPingResponseDto(value: unknown): value is PingResponseDto {
    return isRecord(value) && typeof value.message === "string" && typeof value.host === "string"
        && typeof value.utc === "string" && Number.isFinite(Date.parse(value.utc));
}
