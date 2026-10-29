/** An image with the revision the user saw. */
export interface ImageRevisionRequestDto {
    id: string;
    revision: number;
}

export interface SetImagesArchivedRequestDto {
    projectId: string;
    /** Up to 400 images; all or nothing. */
    images: ImageRevisionRequestDto[];
    archived: boolean;
}
