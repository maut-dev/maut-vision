import type { ImageAnnotationResponseDto } from "@/api/projects/images/response-dto/image-annotation-response-dto";
import type { SaveImageAnnotationRequestDto } from "@/api/projects/images/request-dto/save-image-annotation-request-dto";
import type { ImageReviewState } from "@/api/projects/images/response-dto/image-response-dto";
import { sameBox, type Box } from "./geometry";

/** Объект черновика; у нового объекта локальный ID (newObjectPrefix), при сохранении он уходит как null. */
export interface DraftObject {
    id: string;
    classId: string;
    classVariantId: string | null;
    box: Box;
}

/** Черновик разметки: состояние истории правок редактора. */
export interface AnnotationDraft {
    objects: readonly DraftObject[];
    roi: Box | null;
    reviewState: ImageReviewState;
}

export const newObjectPrefix = "new:";

export function draftOf(annotation: ImageAnnotationResponseDto): AnnotationDraft {
    return { objects: annotation.objects.map(item => ({ ...item, box: { ...item.box } })), roi: annotation.roi, reviewState: annotation.reviewState };
}

/** Черновик отличается от сохранённой ревизии: состав, порядок, классы, варианты, рамки, ROI или проверка. */
export function isDraftChanged(draft: AnnotationDraft, saved: AnnotationDraft): boolean {
    if (draft.reviewState !== saved.reviewState || !sameBox(draft.roi, saved.roi) || draft.objects.length !== saved.objects.length) return true;
    return draft.objects.some((item, index) => {
        const other = saved.objects[index];
        return item.id !== other.id || item.classId !== other.classId || item.classVariantId !== other.classVariantId || !sameBox(item.box, other.box);
    });
}

export function saveRequest(projectId: string, imageId: string, annotationRevision: number, draft: AnnotationDraft): SaveImageAnnotationRequestDto {
    return {
        projectId,
        imageId,
        annotationRevision,
        objects: draft.objects.map(item => ({
            id: item.id.startsWith(newObjectPrefix) ? null : item.id,
            classId: item.classId,
            classVariantId: item.classVariantId,
            box: item.box,
        })),
        roi: draft.roi,
        reviewState: draft.reviewState,
    };
}

export function addObject(draft: AnnotationDraft, classId: string, classVariantId: string | null, box: Box, id = newObjectPrefix + crypto.randomUUID()): AnnotationDraft {
    return { ...draft, objects: [...draft.objects, { id, classId, classVariantId, box }] };
}

export function updateObject(draft: AnnotationDraft, id: string, change: Partial<Omit<DraftObject, "id">>): AnnotationDraft {
    return { ...draft, objects: draft.objects.map(item => item.id === id ? { ...item, ...change } : item) };
}

export function removeObject(draft: AnnotationDraft, id: string): AnnotationDraft {
    return { ...draft, objects: draft.objects.filter(item => item.id !== id) };
}
