import type { ImageRevisionRequestDto } from "./set-images-archived-request-dto";

export interface MoveImagesRequestDto {
    projectId: string;
    sourceGroupId: string;
    sourceRevision: number;
    /** Группа того же происхождения (manual к manual); иначе images.origin_mismatch. */
    targetGroupId: string;
    targetRevision: number;
    /** До 400 активных изображений исходной группы; всё или ничего. */
    images: ImageRevisionRequestDto[];
}
