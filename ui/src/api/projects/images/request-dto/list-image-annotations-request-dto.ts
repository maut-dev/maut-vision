export interface ListImageAnnotationsRequestDto {
    projectId: string;
    /** Up to 200 images, usually one page of the gallery. */
    imageIds: string[];
}
