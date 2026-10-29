/** A frame on a file of the selection: sourceIndex is an index from selectFiles, left/top the top left corner in pixels of the file. */
export interface BackgroundCropRequestDto {
    sourceIndex: number;
    left: number;
    top: number;
}

/** One frame for the whole video: the top left corner in pixels of the frame. */
export interface BackgroundRegionRequestDto {
    left: number;
    top: number;
}

/** "Blur backgrounds": the radius from/to 0–1000 px; seed 0–4 294 967 295 or null ("From the operation"). */
export interface BackgroundBlurRequestDto {
    minRadius: number;
    maxRadius: number;
    seed?: number | null;
}

/**
 * A preparation run (backgrounds.import) and its samples (backgrounds.previewImport) share one form: samples with the same operationId
 * match the future backgrounds. import binds selectionToken to operationId (a choice serves one run), previewImport does not.
 *
 * "Ready frames": without sourceIndices and crops, all files of the selection; those of the right size are taken as they are, the rest are skipped.
 * "Cut from images": sourceIndices are the included files in order (up to 500), crops are frames for files larger than the set;
 * a file without a frame must be exactly the set size.
 * "Cut from video": region, startMs, intervalMs and count ("Backgrounds to cut", 1…availableVideoFrames) are required.
 */
export interface ImportBackgroundsRequestDto {
    projectId: string;
    setId: string;
    selectionToken: string;
    /** Created by the UI for one run; a repeat of import with the same value returns the same job. */
    operationId: string;
    sourceIndices?: number[];
    crops?: BackgroundCropRequestDto[];
    region?: BackgroundRegionRequestDto;
    /** Video: "Start at", ms. */
    startMs?: number;
    /** Video: "Take a shot every", ms (≥ 1). */
    intervalMs?: number;
    /** Video: how many backgrounds to cut; files: an optional limit of added ones. */
    count?: number;
    /** "Skip repeated shots": almost identical (≤ 3/255) to the set and to shots already taken. False by default. */
    skipSimilar?: boolean;
    blur?: BackgroundBlurRequestDto | null;
}
