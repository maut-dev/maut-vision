export interface DeleteBackgroundSetRequestDto {
    projectId: string;
    setId: string;
    /** The revision from reviewSetDeletion. */
    reviewRevision: string;
}
