export interface DeleteImageGroupRequestDto {
    projectId: string;
    groupId: string;
    /** revision из images.reviewGroupDeletion. */
    reviewRevision: string;
}
