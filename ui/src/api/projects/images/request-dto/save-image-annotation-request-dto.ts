import type { ImageBoxDto } from "../response-dto/image-annotation-response-dto";
import type { ImageReviewState } from "../response-dto/image-response-dto";

/** id: an object of the current revision or null for a new one (the host assigns the ID); classVariantId: a variant of this class or null. */
export interface AnnotationObjectRequestDto {
    id: string | null;
    classId: string;
    classVariantId: string | null;
    box: ImageBoxDto;
}

export interface SaveImageAnnotationRequestDto {
    projectId: string;
    imageId: string;
    /** The annotationRevision of the image the user saw; a mismatch gives images.revision_conflict. */
    annotationRevision: number;
    objects: AnnotationObjectRequestDto[];
    roi: ImageBoxDto | null;
    reviewState: ImageReviewState;
}
