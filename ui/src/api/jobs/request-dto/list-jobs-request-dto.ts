export interface ListJobsRequestDto {
    projectId: string;
    /** 1–200, по умолчанию 50. */
    limit?: number;
    /** ID последней задачи предыдущей страницы; задачи идут от новых к старым. */
    before?: string;
}
