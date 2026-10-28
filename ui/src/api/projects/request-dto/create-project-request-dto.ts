import type { InputSizeDto } from "../input-size-dto";

export interface CreateProjectRequestDto {
    name: string;
    description: string;
    baseModelId: string;
    inputSize: InputSizeDto;
}
