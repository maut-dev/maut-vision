import type { InputSizeDto } from "../input-size-dto";

export interface UpdateProjectRequestDto {
    projectId: string;
    /** Ревизия, которую видел пользователь; при расхождении приходит projects.revision_conflict. */
    revision: number;
    name: string;
    description: string;
    baseModelId: string;
    inputSize: InputSizeDto;
}
