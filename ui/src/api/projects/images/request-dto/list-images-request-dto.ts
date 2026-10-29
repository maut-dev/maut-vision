import type { ImageReviewState } from "../response-dto/image-response-dto";

/** active: only active ones (the default), archived: only archived ones, all: every one. */
export type ImageArchiveFilter = "active" | "archived" | "all";

export interface ListImagesRequestDto {
    projectId: string;
    groupId: string;
    archive?: ImageArchiveFilter;
    /** null or absent: any. */
    reviewState?: ImageReviewState | null;
    /** 1–200, 100 by default. */
    limit?: number;
    /** nextCursor of the previous page of the same filter. */
    after?: string;
}
