import { isResourceUrl, type ResourceUrl } from "../../../resources/resource-url";
import { isRecord } from "../../../transport/envelopes";
import { isCount } from "../../catalog/catalog-guards";

/** exact: exactly the set size, larger: a frame can be cut, smaller: will be skipped, unreadable: cannot be read. */
export type BackgroundSourceFit = "exact" | "larger" | "smaller" | "unreadable";

const fits: readonly string[] = ["exact", "larger", "smaller", "unreadable"] satisfies BackgroundSourceFit[];

/** A file of the selection: index is its number in the selection (it goes to previewSource, sourceIndices, crops); the size follows the orientation. */
export interface BackgroundSourceFileResponseDto {
    index: number;
    name: string;
    width: number | null;
    height: number | null;
    fit: BackgroundSourceFit;
}

/**
 * A file selection (backgrounds.selectFiles). selectionToken is null when the dialog was closed (files is empty); it is valid for 30 minutes and only
 * on this page. Files come in the natural order of names; exact/larger/smaller/unreadable are counters for the set size
 * targetWidth × targetHeight: "Ready frames" take the exact ones, the rest are skipped.
 */
export interface BackgroundFileSelectionResponseDto {
    selectionToken: string | null;
    targetWidth: number;
    targetHeight: number;
    files: BackgroundSourceFileResponseDto[];
    exact: number;
    larger: number;
    smaller: number;
    unreadable: number;
}

function isSourceFile(value: unknown): value is BackgroundSourceFileResponseDto {
    return isRecord(value) && isCount(value.index) && typeof value.name === "string"
        && (value.width === null || isCount(value.width)) && (value.height === null || isCount(value.height))
        && typeof value.fit === "string" && fits.includes(value.fit);
}

export function isBackgroundFileSelectionResponseDto(value: unknown): value is BackgroundFileSelectionResponseDto {
    return isRecord(value) && (value.selectionToken === null || (typeof value.selectionToken === "string" && value.selectionToken.length > 0))
        && isCount(value.targetWidth) && isCount(value.targetHeight) && Array.isArray(value.files) && value.files.every(isSourceFile)
        && isCount(value.exact) && isCount(value.larger) && isCount(value.smaller) && isCount(value.unreadable);
}

/**
 * An image for the crop frame: sourceIndex is a file of the selection (null for a video), timestampMs the mark of a video frame (null for a file). width/height
 * are the size of the source image: frame coordinates are counted in it. previewUrl is a reduced copy previewWidth × previewHeight
 * (at most 1600 px), temporary: valid for a limited time and on this page only.
 */
export interface BackgroundSourcePreviewResponseDto {
    sourceIndex: number | null;
    timestampMs: number | null;
    name: string;
    width: number;
    height: number;
    previewUrl: ResourceUrl;
    previewWidth: number;
    previewHeight: number;
}

export function isBackgroundSourcePreviewResponseDto(value: unknown): value is BackgroundSourcePreviewResponseDto {
    return isRecord(value) && (value.sourceIndex === null || isCount(value.sourceIndex)) && (value.timestampMs === null || isCount(value.timestampMs))
        && typeof value.name === "string" && isCount(value.width) && isCount(value.height) && isResourceUrl(value.previewUrl)
        && isCount(value.previewWidth) && isCount(value.previewHeight);
}
