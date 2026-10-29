export interface ListActiveJobsRequestDto {
    projectId: string;
    /** 1–200, 30 by default. */
    limit?: number;
    /** nextCursor of the previous page; jobs go in queue order. */
    after?: string;
}
