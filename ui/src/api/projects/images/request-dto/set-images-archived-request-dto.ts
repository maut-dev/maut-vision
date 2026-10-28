/** Изображение с ревизией, которую видел пользователь. */
export interface ImageRevisionRequestDto {
    id: string;
    revision: number;
}

export interface SetImagesArchivedRequestDto {
    projectId: string;
    /** До 400 изображений; всё или ничего. */
    images: ImageRevisionRequestDto[];
    archived: boolean;
}
