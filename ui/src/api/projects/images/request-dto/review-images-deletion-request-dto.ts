export interface ReviewImagesDeletionRequestDto {
    projectId: string;
    groupId: string;
    /** Up to 400 images of this group. */
    imageIds: string[];
}
