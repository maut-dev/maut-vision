import { isRecord } from "../../../transport/envelopes";
import { isDate, isId, isRevision } from "../../catalog/catalog-guards";
import { isImageResponseDto, isImageReviewState, type ImageResponseDto, type ImageReviewState } from "./image-response-dto";

/**
 * A box in fractions 0–1 of the oriented frame from the top left corner: x, y ≥ 0, width, height > 0, x + width ≤ 1,
 * y + height ≤ 1 (tolerance 1e-9). A wrong box is rejected (images.box_invalid), not clipped.
 */
export interface ImageBoxDto {
    x: number;
    y: number;
    width: number;
    height: number;
}

/** An annotation object; classVariantId is a class variant (binding) of this class or null ("No variant"). */
export interface AnnotationObjectResponseDto {
    id: string;
    classId: string;
    classVariantId: string | null;
    box: ImageBoxDto;
}

/** An immutable annotation revision. roi is the "ROI area" box or null. */
export interface ImageAnnotationResponseDto {
    imageId: string;
    revision: number;
    objects: AnnotationObjectResponseDto[];
    roi: ImageBoxDto | null;
    reviewState: ImageReviewState;
    createdAt: string;
}

/** The result of saving: the image with new revisions and the saved annotation revision (with IDs of new objects). */
export interface SavedImageAnnotationResponseDto {
    image: ImageResponseDto;
    annotation: ImageAnnotationResponseDto;
}

const isFiniteNumber = (value: unknown): value is number => typeof value === "number" && Number.isFinite(value);

export function isImageBoxDto(value: unknown): value is ImageBoxDto {
    return isRecord(value) && isFiniteNumber(value.x) && isFiniteNumber(value.y) && isFiniteNumber(value.width) && isFiniteNumber(value.height);
}

function isAnnotationObjectResponseDto(value: unknown): value is AnnotationObjectResponseDto {
    return isRecord(value) && isId(value.id) && isId(value.classId) && (value.classVariantId === null || isId(value.classVariantId))
        && isImageBoxDto(value.box);
}

export function isImageAnnotationResponseDto(value: unknown): value is ImageAnnotationResponseDto {
    return isRecord(value) && isId(value.imageId) && isRevision(value.revision)
        && Array.isArray(value.objects) && value.objects.every(isAnnotationObjectResponseDto)
        && (value.roi === null || isImageBoxDto(value.roi)) && isImageReviewState(value.reviewState) && isDate(value.createdAt);
}

export function isImageAnnotationList(value: unknown): value is ImageAnnotationResponseDto[] {
    return Array.isArray(value) && value.every(isImageAnnotationResponseDto);
}

export function isSavedImageAnnotationResponseDto(value: unknown): value is SavedImageAnnotationResponseDto {
    return isRecord(value) && isImageResponseDto(value.image) && isImageAnnotationResponseDto(value.annotation);
}
