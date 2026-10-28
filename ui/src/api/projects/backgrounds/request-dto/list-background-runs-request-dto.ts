export interface ListBackgroundRunsRequestDto {
    projectId: string;
    setId: string;
    /** 1–200, по умолчанию 50. */
    limit?: number;
    /** ID последней задачи предыдущей страницы. */
    before?: string;
}
