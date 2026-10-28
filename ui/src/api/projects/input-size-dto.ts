import { isRecord } from "../transport/envelopes";

export interface InputSizeDto {
    width: number;
    height: number;
}

export function isInputSizeDto(value: unknown): value is InputSizeDto {
    return isRecord(value) && Number.isSafeInteger(value.width) && (value.width as number) > 0
        && Number.isSafeInteger(value.height) && (value.height as number) > 0;
}
