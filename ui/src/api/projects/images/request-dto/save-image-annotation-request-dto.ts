import type { ImageBoxDto } from "../response-dto/image-annotation-response-dto";
import type { ImageReviewState } from "../response-dto/image-response-dto";

/** id — объект текущей ревизии или null для нового (ID назначит host); classVariantId — вариант этого класса или null. */
export interface AnnotationObjectRequestDto {
    id: string | null;
    classId: string;
    classVariantId: string | null;
    box: ImageBoxDto;
}

export interface SaveImageAnnotationRequestDto {
    projectId: string;
    imageId: string;
    /** annotationRevision изображения, которое видел пользователь; при расхождении — images.revision_conflict. */
    annotationRevision: number;
    objects: AnnotationObjectRequestDto[];
    roi: ImageBoxDto | null;
    reviewState: ImageReviewState;
}
