import type { ImageSplitHint } from "../response-dto/image-group-response-dto";

export interface UpdateImageGroupRequestDto {
    projectId: string;
    groupId: string;
    name: string;
    description: string;
    splitHint: ImageSplitHint;
    /** Ревизия группы, которую видел пользователь; при расхождении — images.revision_conflict. */
    revision: number;
}
