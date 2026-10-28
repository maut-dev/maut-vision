import type { ImageSplitHint } from "../response-dto/image-group-response-dto";

export interface CreateImageGroupRequestDto {
    projectId: string;
    /** 1–120 символов после обрезки пробелов; повторы допустимы. */
    name: string;
    /** До 4000 символов. */
    description: string;
    splitHint: ImageSplitHint;
}
