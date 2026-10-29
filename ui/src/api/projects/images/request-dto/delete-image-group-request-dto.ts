export interface DeleteImageGroupRequestDto {
    projectId: string;
    groupId: string;
    /** The revision from images.reviewGroupDeletion. */
    reviewRevision: string;
}
