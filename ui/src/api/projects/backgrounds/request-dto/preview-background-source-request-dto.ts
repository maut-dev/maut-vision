/** A file of the selection for the crop frame. */
export interface PreviewBackgroundSourceRequestDto {
    projectId: string;
    /** A token from backgrounds.selectFiles; a preview does not bind it to an operation. */
    selectionToken: string;
    /** The index of a file from the answer of selectFiles. */
    sourceIndex: number;
    /** 64–4096, 1600 by default. */
    maxSide?: number;
}
