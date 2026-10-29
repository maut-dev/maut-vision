import { isRecord } from "../../transport/envelopes";
import { isProjectResponseDto, type ProjectResponseDto } from "./project-response-dto";

/** The library: all projects and the last opened one (null when there is none); the last one is shown first, marked Last opened. */
export interface ProjectLibraryResponseDto {
    items: ProjectResponseDto[];
    lastProjectId: string | null;
}

export function isProjectLibraryResponseDto(value: unknown): value is ProjectLibraryResponseDto {
    return isRecord(value) && Array.isArray(value.items) && value.items.every(isProjectResponseDto)
        && (value.lastProjectId === null || (typeof value.lastProjectId === "string" && value.items.some(item => item.id === value.lastProjectId)));
}
