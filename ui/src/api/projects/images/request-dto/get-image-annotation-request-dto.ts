export interface GetImageAnnotationRequestDto {
    projectId: string;
    imageId: string;
    /** Ревизия истории; отсутствует — текущая. */
    revision?: number;
}
