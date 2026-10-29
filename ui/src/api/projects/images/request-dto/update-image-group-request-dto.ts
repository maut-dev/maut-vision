import type { ImageSplitHint } from "../response-dto/image-group-response-dto";

export interface UpdateImageGroupRequestDto {
    projectId: string;
    groupId: string;
    name: string;
    description: string;
    splitHint: ImageSplitHint;
    /** The group revision the user saw; a mismatch gives images.revision_conflict. */
    revision: number;
}
