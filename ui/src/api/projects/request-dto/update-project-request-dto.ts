import type { InputSizeDto } from "../input-size-dto";

export interface UpdateProjectRequestDto {
    projectId: string;
    /** The revision the user saw; on a mismatch projects.revision_conflict comes back. */
    revision: number;
    name: string;
    description: string;
    baseModelId: string;
    inputSize: InputSizeDto;
}
