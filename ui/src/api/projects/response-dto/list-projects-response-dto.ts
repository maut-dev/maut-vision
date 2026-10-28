import { isRecord } from "../../transport/envelopes";
import { isProjectResponseDto, type ProjectResponseDto } from "./project-response-dto";

/** Библиотека: все проекты и последний открытый (null, если его нет); последний показывается первым с отметкой «Последний открытый». */
export interface ProjectLibraryResponseDto {
    items: ProjectResponseDto[];
    lastProjectId: string | null;
}

export function isProjectLibraryResponseDto(value: unknown): value is ProjectLibraryResponseDto {
    return isRecord(value) && Array.isArray(value.items) && value.items.every(isProjectResponseDto)
        && (value.lastProjectId === null || (typeof value.lastProjectId === "string" && value.items.some(item => item.id === value.lastProjectId)));
}
