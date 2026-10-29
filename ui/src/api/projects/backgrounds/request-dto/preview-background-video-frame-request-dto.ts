/** A frame of the chosen video for the crop frame: "the example shot in the middle" and "Show another shot". */
export interface PreviewBackgroundVideoFrameRequestDto {
    projectId: string;
    /** A token from backgrounds.selectVideo. */
    selectionToken: string;
    /** A mark ≥ 0 and before the end of the video; the first frame not earlier than it is taken. */
    timestampMs: number;
    /** 64–4096, 1600 by default. */
    maxSide?: number;
}
