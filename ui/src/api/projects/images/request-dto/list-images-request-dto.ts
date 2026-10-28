import type { ImageReviewState } from "../response-dto/image-response-dto";

/** active — только активные (по умолчанию), archived — только архивные, all — все. */
export type ImageArchiveFilter = "active" | "archived" | "all";

export interface ListImagesRequestDto {
    projectId: string;
    groupId: string;
    archive?: ImageArchiveFilter;
    /** null или отсутствует — любые. */
    reviewState?: ImageReviewState | null;
    /** 1–200, по умолчанию 100. */
    limit?: number;
    /** nextCursor предыдущей страницы того же фильтра. */
    after?: string;
}
