export interface ReviewBackgroundsDeletionRequestDto {
    projectId: string;
    setId: string;
    /** Up to 400 backgrounds of this set. */
    itemIds: string[];
}
