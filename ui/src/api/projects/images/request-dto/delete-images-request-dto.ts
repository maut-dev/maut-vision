export interface DeleteImagesRequestDto {
    projectId: string;
    groupId: string;
    imageIds: string[];
    /** The revision from images.reviewDeletion for the same imageIds. */
    reviewRevision: string;
}
