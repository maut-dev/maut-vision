import { isRecord } from "../../../transport/envelopes";
import { isCount } from "../../catalog/catalog-guards";

/**
 * A video from images.selectVideo. selectionToken is null when the user closed the dialog (then the other fields are null); it is valid for
 * 30 minutes and on this page only. displayName is the file name without a path, byteSize the file size, width/height the size
 * of frames after rotation, durationMs the duration. The video is not hashed when chosen.
 */
export interface VideoSelectionResponseDto {
    selectionToken: string | null;
    displayName: string | null;
    byteSize: number | null;
    width: number | null;
    height: number | null;
    durationMs: number | null;
}

/** A chosen video: all fields are set. */
export type SelectedVideoDto = { [Key in keyof VideoSelectionResponseDto]: NonNullable<VideoSelectionResponseDto[Key]> };

const isPositive = (value: unknown): value is number => isCount(value) && value > 0;

export function isVideoSelectionResponseDto(value: unknown): value is VideoSelectionResponseDto {
    if (!isRecord(value)) return false;
    if (value.selectionToken === null) {
        return value.displayName === null && value.byteSize === null && value.width === null && value.height === null && value.durationMs === null;
    }
    return typeof value.selectionToken === "string" && value.selectionToken.length > 0 && typeof value.displayName === "string"
        && isPositive(value.byteSize) && isPositive(value.width) && isPositive(value.height) && isPositive(value.durationMs);
}

/** true when the user chose a video (and did not close the dialog). */
export function isSelectedVideo(value: VideoSelectionResponseDto): value is SelectedVideoDto {
    return value.selectionToken !== null;
}
