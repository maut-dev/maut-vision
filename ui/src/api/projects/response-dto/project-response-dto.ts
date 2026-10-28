import { isRecord } from "../../transport/envelopes";
import { isInputSizeDto, type InputSizeDto } from "../input-size-dto";

export interface ProjectResponseDto {
    id: string;
    name: string;
    description: string;
    baseModelId: string;
    inputSize: InputSizeDto;
    revision: number;
    createdAt: string;
    updatedAt: string;
    archivedAt: string | null;
}

const isDate = (value: unknown) => typeof value === "string" && Number.isFinite(Date.parse(value));

export function isProjectResponseDto(value: unknown): value is ProjectResponseDto {
    return isRecord(value) && typeof value.id === "string" && value.id.length > 0
        && typeof value.name === "string" && value.name.trim().length > 0
        && typeof value.description === "string"
        && typeof value.baseModelId === "string" && value.baseModelId.length > 0
        && isInputSizeDto(value.inputSize)
        && Number.isSafeInteger(value.revision) && (value.revision as number) >= 1
        && isDate(value.createdAt) && isDate(value.updatedAt)
        && (value.archivedAt === null || isDate(value.archivedAt));
}
