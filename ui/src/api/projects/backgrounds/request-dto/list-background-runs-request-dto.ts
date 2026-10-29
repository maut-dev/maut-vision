export interface ListBackgroundRunsRequestDto {
    projectId: string;
    setId: string;
    /** 1–200, 50 by default. */
    limit?: number;
    /** The ID of the last job of the previous page. */
    before?: string;
}
