export interface ListActiveJobsRequestDto {
    projectId: string;
    /** 1–200, по умолчанию 30. */
    limit?: number;
    /** nextCursor предыдущей страницы; задачи идут в порядке очереди. */
    after?: string;
}
