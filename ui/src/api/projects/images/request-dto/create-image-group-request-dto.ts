import type { ImageSplitHint } from "../response-dto/image-group-response-dto";

export interface CreateImageGroupRequestDto {
    projectId: string;
    /** 1–120 characters after trimming; duplicates are allowed. */
    name: string;
    /** Up to 4000 characters. */
    description: string;
    splitHint: ImageSplitHint;
}
