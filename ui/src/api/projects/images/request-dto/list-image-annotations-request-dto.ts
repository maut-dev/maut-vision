export interface ListImageAnnotationsRequestDto {
    projectId: string;
    /** До 200 изображений, обычно одна страница галереи. */
    imageIds: string[];
}
