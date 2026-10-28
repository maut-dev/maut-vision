export interface DeleteBackgroundSetRequestDto {
    projectId: string;
    setId: string;
    /** revision из reviewSetDeletion. */
    reviewRevision: string;
}
