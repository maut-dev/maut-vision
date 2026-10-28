import { isRecord } from "../../../transport/envelopes";
import { isDate, isId, isRevision } from "../../catalog/catalog-guards";
import { isImageResponseDto, isImageReviewState, type ImageResponseDto, type ImageReviewState } from "./image-response-dto";

/**
 * Рамка в долях 0–1 ориентированного кадра от левого верхнего угла: x, y ≥ 0, width, height > 0, x + width ≤ 1,
 * y + height ≤ 1 (допуск 1e-9). Некорректная рамка отклоняется (images.box_invalid), а не обрезается.
 */
export interface ImageBoxDto {
    x: number;
    y: number;
    width: number;
    height: number;
}

/** Объект разметки; classVariantId — вариант класса (привязка) этого класса или null («Без варианта»). */
export interface AnnotationObjectResponseDto {
    id: string;
    classId: string;
    classVariantId: string | null;
    box: ImageBoxDto;
}

/** Неизменяемая ревизия разметки. roi — рамка «Область ROI» или null. */
export interface ImageAnnotationResponseDto {
    imageId: string;
    revision: number;
    objects: AnnotationObjectResponseDto[];
    roi: ImageBoxDto | null;
    reviewState: ImageReviewState;
    createdAt: string;
}

/** Результат сохранения: изображение с новыми ревизиями и сохранённая ревизия разметки (с ID новых объектов). */
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
