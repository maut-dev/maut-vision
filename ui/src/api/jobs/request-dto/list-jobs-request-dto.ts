export interface ListJobsRequestDto {
    projectId: string;
    /** 1–200, 50 by default. */
    limit?: number;
    /** The ID of the last job of the previous page; jobs go from newest to oldest. */
    before?: string;
}
