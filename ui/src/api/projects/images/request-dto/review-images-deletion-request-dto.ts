export interface ReviewImagesDeletionRequestDto {
    projectId: string;
    groupId: string;
    /** До 400 изображений этой группы. */
    imageIds: string[];
}
