import { isResourceUrl, type ResourceUrl } from "../../../resources/resource-url";
import { isRecord } from "../../../transport/envelopes";
import { isCount, isDate, isDateOrNull, isId, isRevision } from "../../catalog/catalog-guards";
import { isImageGroupResponseDto, type ImageGroupResponseDto } from "./image-group-response-dto";

export type ImageFormat = "png" | "jpeg" | "webp" | "bmp";

/** reviewed: the annotation is reviewed, including a deliberately empty frame. */
export type ImageReviewState = "unreviewed" | "reviewed";

export type ImageLineageKind = "file" | "video" | "generated";

const formats: readonly string[] = ["png", "jpeg", "webp", "bmp"] satisfies ImageFormat[];
const reviewStates: readonly string[] = ["unreviewed", "reviewed"] satisfies ImageReviewState[];
const lineageKinds: readonly string[] = ["file", "video", "generated"] satisfies ImageLineageKind[];

export const isImageReviewState = (value: unknown): value is ImageReviewState => typeof value === "string" && reviewStates.includes(value);

/**
 * The origin of a frame ("Source: …"). sourceFamilyId joins frames of one source (file:<pixelHash>,
 * video:<video hash>). Video and generation fields are null for other kinds.
 */
export interface ImageLineageDto {
    kind: ImageLineageKind;
    sourceFamilyId: string;
    videoName: string | null;
    timestampMs: number | null;
    frameIndex: number | null;
    generationRunId: string | null;
    sampleIndex: number | null;
    recipeRevision: number | null;
}

/**
 * An image of a group. width/height follow the EXIF orientation, as <img> shows it; annotation coordinates refer to
 * this frame. revision grows on saving an annotation, archiving and moving (it goes to setArchived and move);
 * annotationRevision is the current annotation revision (it goes to saveAnnotation). objectCount and reviewState come from the current
 * annotation ("W × H · boxes: N / no boxes · reviewed"). originalUrl is for the viewer, thumbnailUrl (JPEG up to 256 px)
 * for the gallery; the URLs are valid for a limited time and on this page only.
 */
export interface ImageResponseDto {
    id: string;
    groupId: string;
    displayName: string;
    format: ImageFormat;
    width: number;
    height: number;
    byteSize: number;
    revision: number;
    annotationRevision: number;
    objectCount: number;
    reviewState: ImageReviewState;
    createdAt: string;
    archivedAt: string | null;
    lineage: ImageLineageDto;
    originalUrl: ResourceUrl;
    thumbnailUrl: ResourceUrl;
}

const isCountOrNull = (value: unknown) => value === null || isCount(value);

function isImageLineageDto(value: unknown): value is ImageLineageDto {
    return isRecord(value) && typeof value.kind === "string" && lineageKinds.includes(value.kind) && typeof value.sourceFamilyId === "string"
        && (value.videoName === null || typeof value.videoName === "string") && isCountOrNull(value.timestampMs) && isCountOrNull(value.frameIndex)
        && (value.generationRunId === null || isId(value.generationRunId)) && isCountOrNull(value.sampleIndex) && isCountOrNull(value.recipeRevision);
}

export function isImageResponseDto(value: unknown): value is ImageResponseDto {
    return isRecord(value) && isId(value.id) && isId(value.groupId) && typeof value.displayName === "string"
        && typeof value.format === "string" && formats.includes(value.format)
        && isCount(value.width) && (value.width as number) > 0 && isCount(value.height) && (value.height as number) > 0
        && isCount(value.byteSize) && isRevision(value.revision) && isRevision(value.annotationRevision) && isCount(value.objectCount)
        && isImageReviewState(value.reviewState) && isDate(value.createdAt) && isDateOrNull(value.archivedAt) && isImageLineageDto(value.lineage)
        && isResourceUrl(value.originalUrl) && isResourceUrl(value.thumbnailUrl);
}

export function isImageList(value: unknown): value is ImageResponseDto[] {
    return Array.isArray(value) && value.every(isImageResponseDto);
}

/** A page in import order; nextCursor is the after value for the next page of the same filter, null on the last one. */
export interface ImagePageResponseDto {
    items: ImageResponseDto[];
    nextCursor: string | null;
}

export function isImagePageResponseDto(value: unknown): value is ImagePageResponseDto {
    return isRecord(value) && isImageList(value.items)
        && (value.nextCursor === null || (typeof value.nextCursor === "string" && value.nextCursor.length > 0));
}

/** The result of a move: both groups with new revisions and the moved images (at the end of the target order). */
export interface MovedImagesResponseDto {
    source: ImageGroupResponseDto;
    target: ImageGroupResponseDto;
    images: ImageResponseDto[];
}

export function isMovedImagesResponseDto(value: unknown): value is MovedImagesResponseDto {
    return isRecord(value) && isImageGroupResponseDto(value.source) && isImageGroupResponseDto(value.target) && isImageList(value.images);
}

/**
 * A choice in the native dialog. selectionToken is null when the user closed the dialog; the token is valid for 30 minutes and only on
 * this page. For files fileCount is their number, for a folder folderName is its name without a path (the job finds the contents).
 */
export interface ImageSelectionResponseDto {
    kind: "files" | "folder";
    selectionToken: string | null;
    fileCount: number | null;
    folderName: string | null;
}

export function isImageSelectionResponseDto(value: unknown): value is ImageSelectionResponseDto {
    return isRecord(value) && (value.kind === "files" || value.kind === "folder")
        && (value.selectionToken === null || (typeof value.selectionToken === "string" && value.selectionToken.length > 0))
        && isCountOrNull(value.fileCount) && (value.folderName === null || typeof value.folderName === "string");
}
