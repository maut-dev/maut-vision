/** A background with the revision the user saw. */
export interface BackgroundRevisionRequestDto {
    id: string;
    revision: number;
}

export interface SetBackgroundsRemovedRequestDto {
    projectId: string;
    /** Up to 400 backgrounds; all or nothing. No set revision is needed: running preparation does not interfere. */
    items: BackgroundRevisionRequestDto[];
    /** true: "Remove", false: "Restore". */
    removed: boolean;
}
