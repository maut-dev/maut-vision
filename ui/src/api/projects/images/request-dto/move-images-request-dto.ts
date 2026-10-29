import type { ImageRevisionRequestDto } from "./set-images-archived-request-dto";

export interface MoveImagesRequestDto {
    projectId: string;
    sourceGroupId: string;
    sourceRevision: number;
    /** A group of the same origin (manual to manual); otherwise images.origin_mismatch. */
    targetGroupId: string;
    targetRevision: number;
    /** Up to 400 active images of the source group; all or nothing. */
    images: ImageRevisionRequestDto[];
}
