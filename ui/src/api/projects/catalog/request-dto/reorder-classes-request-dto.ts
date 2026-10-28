export interface ReorderClassesRequestDto {
    projectId: string;
    /** Все классы проекта, включая архивные, в новом порядке. */
    classIds: string[];
    /** ClassOrderRevision из снимка каталога. */
    orderRevision: number;
}
