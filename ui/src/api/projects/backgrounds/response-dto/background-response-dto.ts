import { isResourceUrl, type ResourceUrl } from "../../../resources/resource-url";
import { isRecord } from "../../../transport/envelopes";
import { isCount, isDate, isDateOrNull, isId, isRevision } from "../../catalog/catalog-guards";

/** uploaded: "Ready image", image-crop: "Image fragment", video-frame: "Video frame". */
export type BackgroundOriginKind = "uploaded" | "image-crop" | "video-frame";

const originKinds: readonly string[] = ["uploaded", "image-crop", "video-frame"] satisfies BackgroundOriginKind[];

/** The cut region of the source image in its pixels: "Region: x, y · w × h px". */
export interface BackgroundCropAreaDto {
    left: number;
    top: number;
    width: number;
    height: number;
}

/**
 * The origin of a background. sourceName is the name of the file or the video (the card caption); videoName, timestampMs, frameIndex only for a video
 * frame. crop gives "cropped at x L, y T" (null: the whole image). blurRadius gives "blur r px" (0: sharp), null: no blur.
 */
export interface BackgroundOriginDto {
    kind: BackgroundOriginKind;
    operationId: string;
    sourceName: string;
    videoName: string | null;
    timestampMs: number | null;
    frameIndex: number | null;
    crop: BackgroundCropAreaDto | null;
    blurRadius: number | null;
    processorVersion: string;
}

/**
 * A background is a PNG of exactly the set size. The revision goes to setRemoved; removedAt: removed ("Restore"). originalUrl is for
 * the viewer, thumbnailUrl (JPEG up to 256 px) for the gallery; the URLs are valid for a limited time and on this page only.
 */
export interface BackgroundResponseDto {
    id: string;
    setId: string;
    width: number;
    height: number;
    byteSize: number;
    revision: number;
    createdAt: string;
    removedAt: string | null;
    origin: BackgroundOriginDto;
    originalUrl: ResourceUrl;
    thumbnailUrl: ResourceUrl;
}

const isCountOrNull = (value: unknown) => value === null || isCount(value);
const isRadius = (value: unknown) => typeof value === "number" && Number.isFinite(value) && value >= 0;

export function isBackgroundCropAreaDto(value: unknown): value is BackgroundCropAreaDto {
    return isRecord(value) && isCount(value.left) && isCount(value.top) && isCount(value.width) && isCount(value.height);
}

function isBackgroundOriginDto(value: unknown): value is BackgroundOriginDto {
    return isRecord(value) && typeof value.kind === "string" && originKinds.includes(value.kind) && typeof value.operationId === "string"
        && typeof value.sourceName === "string" && (value.videoName === null || typeof value.videoName === "string")
        && isCountOrNull(value.timestampMs) && isCountOrNull(value.frameIndex)
        && (value.crop === null || isBackgroundCropAreaDto(value.crop)) && (value.blurRadius === null || isRadius(value.blurRadius))
        && typeof value.processorVersion === "string";
}

export function isBackgroundResponseDto(value: unknown): value is BackgroundResponseDto {
    return isRecord(value) && isId(value.id) && isId(value.setId) && isCount(value.width) && isCount(value.height) && isCount(value.byteSize)
        && isRevision(value.revision) && isDate(value.createdAt) && isDateOrNull(value.removedAt) && isBackgroundOriginDto(value.origin)
        && isResourceUrl(value.originalUrl) && isResourceUrl(value.thumbnailUrl);
}

export function isBackgroundList(value: unknown): value is BackgroundResponseDto[] {
    return Array.isArray(value) && value.every(isBackgroundResponseDto);
}

/** A page in the order of adding; nextCursor is the after value for the next page of the same filter, null on the last one. */
export interface BackgroundPageResponseDto {
    items: BackgroundResponseDto[];
    nextCursor: string | null;
}

export function isBackgroundPageResponseDto(value: unknown): value is BackgroundPageResponseDto {
    return isRecord(value) && isBackgroundList(value.items)
        && (value.nextCursor === null || (typeof value.nextCursor === "string" && value.nextCursor.length > 0));
}
