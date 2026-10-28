export interface ReviewBackgroundsDeletionRequestDto {
    projectId: string;
    setId: string;
    /** До 400 фонов этого набора. */
    itemIds: string[];
}
