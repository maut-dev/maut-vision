export interface GetImageAnnotationRequestDto {
    projectId: string;
    imageId: string;
    /** A revision of the history; absent means the current one. */
    revision?: number;
}
