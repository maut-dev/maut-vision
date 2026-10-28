import { isBaseModelResponseDto, type BaseModelResponseDto } from "./base-model-response-dto";

export type ListBaseModelsResponseDto = BaseModelResponseDto[];

export function isListBaseModelsResponseDto(value: unknown): value is ListBaseModelsResponseDto {
    return Array.isArray(value) && value.every(isBaseModelResponseDto);
}
