export interface DeleteImagesRequestDto {
    projectId: string;
    groupId: string;
    imageIds: string[];
    /** revision из images.reviewDeletion для тех же imageIds. */
    reviewRevision: string;
}
