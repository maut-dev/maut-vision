import { isRecord } from "../../../transport/envelopes";
import { isCount, isId } from "../../catalog/catalog-guards";
import { isDeletionBlockerResponseDto, type DeletionBlockerResponseDto } from "../../catalog/response-dto/deletion-review-response-dto";

/**
 * The review of a permanent deletion: the whole group (wholeGroup) or selected images. "Images to delete: N" is images;
 * non-empty blockers give "Deletion is not available yet:" with blockerText lines (images.import_active: an import into this group; later
 * generation). The revision goes to the deletion; a change of the contents gives images.deletion_revision_conflict.
 */
export interface ImageDeletionReviewResponseDto {
    groupId: string;
    groupName: string;
    wholeGroup: boolean;
    images: number;
    blockers: DeletionBlockerResponseDto[];
    revision: string;
}

/** Rows are deleted; cleanupPending: files are still being deleted in the background, cleanup continues the next time the project opens. */
export interface ImageDeletionResultResponseDto {
    groupId: string;
    groupDeleted: boolean;
    images: number;
    cleanupPending: boolean;
}

export function isImageDeletionReviewResponseDto(value: unknown): value is ImageDeletionReviewResponseDto {
    return isRecord(value) && isId(value.groupId) && typeof value.groupName === "string" && typeof value.wholeGroup === "boolean"
        && isCount(value.images) && Array.isArray(value.blockers) && value.blockers.every(isDeletionBlockerResponseDto)
        && typeof value.revision === "string" && value.revision.length > 0;
}

export function isImageDeletionResultResponseDto(value: unknown): value is ImageDeletionResultResponseDto {
    return isRecord(value) && isId(value.groupId) && typeof value.groupDeleted === "boolean" && isCount(value.images)
        && typeof value.cleanupPending === "boolean";
}
