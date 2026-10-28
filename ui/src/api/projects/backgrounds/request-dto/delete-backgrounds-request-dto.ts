export interface DeleteBackgroundsRequestDto {
    projectId: string;
    setId: string;
    itemIds: string[];
    /** revision из reviewDeletion для тех же itemIds. */
    reviewRevision: string;
}
