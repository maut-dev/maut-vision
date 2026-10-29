export interface ImportImagesRequestDto {
    projectId: string;
    groupId: string;
    /** A token from images.selectFiles, images.selectFolder or images.selectVideo; valid for 30 minutes and on this page only. */
    selectionToken: string;
    /** Created by the UI for one action; a repeat with the same value returns the same job. */
    operationId: string;
    /** For a folder: "Include subfolders". False by default. */
    recursive?: boolean;
    /** "Skip duplicates in the group". True by default. */
    skipDuplicates?: boolean;
    /** For a video (required): the mark of the first frame, ms, ≥ 0. */
    startMs?: number;
    /** For a video (required): the step between frames, ms, ≥ 1. */
    intervalMs?: number;
    /** For a video (required): the frame count ≥ 1; the last mark startMs + (count − 1) × intervalMs is before the end of the video. */
    count?: number;
}
