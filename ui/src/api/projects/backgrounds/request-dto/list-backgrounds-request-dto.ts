/** active — только активные (по умолчанию), removed — только убранные, all — все («Показать убранные фоны»). */
export type BackgroundRemovedFilter = "active" | "removed" | "all";

export interface ListBackgroundsRequestDto {
    projectId: string;
    setId: string;
    removed?: BackgroundRemovedFilter;
    /** 1–200, по умолчанию 100. */
    limit?: number;
    /** nextCursor предыдущей страницы того же фильтра. */
    after?: string;
}
