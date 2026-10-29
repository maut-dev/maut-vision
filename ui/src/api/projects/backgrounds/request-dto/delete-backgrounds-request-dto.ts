export interface DeleteBackgroundsRequestDto {
    projectId: string;
    setId: string;
    itemIds: string[];
    /** The revision from reviewDeletion for the same itemIds. */
    reviewRevision: string;
}
