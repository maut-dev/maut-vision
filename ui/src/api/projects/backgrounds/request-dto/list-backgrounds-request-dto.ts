/** active: only active ones (the default), removed: only removed ones, all: every one ("Show removed backgrounds"). */
export type BackgroundRemovedFilter = "active" | "removed" | "all";

export interface ListBackgroundsRequestDto {
    projectId: string;
    setId: string;
    removed?: BackgroundRemovedFilter;
    /** 1–200, 100 by default. */
    limit?: number;
    /** nextCursor of the previous page of the same filter. */
    after?: string;
}
