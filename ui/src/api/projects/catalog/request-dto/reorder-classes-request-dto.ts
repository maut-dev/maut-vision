export interface ReorderClassesRequestDto {
    projectId: string;
    /** All classes of the project, archived included, in the new order. */
    classIds: string[];
    /** ClassOrderRevision from the catalog snapshot. */
    orderRevision: number;
}
