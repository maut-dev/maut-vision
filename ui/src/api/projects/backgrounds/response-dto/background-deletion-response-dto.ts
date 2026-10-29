import { isRecord } from "../../../transport/envelopes";
import { isCount, isId } from "../../catalog/catalog-guards";
import { isDeletionBlockerResponseDto, type DeletionBlockerResponseDto } from "../../catalog/response-dto/deletion-review-response-dto";

/**
 * The review of a permanent deletion: the whole set (wholeSet, "Delete the background set permanently?") or selected backgrounds ("Delete the backgrounds
 * permanently?"). items gives "Backgrounds to delete: N."; non-empty blockers give "Finish the jobs or change the dependencies before
 * deleting:" with blockerText lines (backgrounds.import_active: preparation into this set; generation presets).
 * The revision goes to the deletion; a change of the contents gives backgrounds.deletion_revision_conflict.
 */
export interface BackgroundDeletionReviewResponseDto {
    setId: string;
    setName: string;
    wholeSet: boolean;
    items: number;
    blockers: DeletionBlockerResponseDto[];
    revision: string;
}

/** Rows are deleted; cleanupPending: files are still being deleted in the background, cleanup continues the next time the project opens. */
export interface BackgroundDeletionResultResponseDto {
    setId: string;
    setDeleted: boolean;
    items: number;
    cleanupPending: boolean;
}

export function isBackgroundDeletionReviewResponseDto(value: unknown): value is BackgroundDeletionReviewResponseDto {
    return isRecord(value) && isId(value.setId) && typeof value.setName === "string" && typeof value.wholeSet === "boolean"
        && isCount(value.items) && Array.isArray(value.blockers) && value.blockers.every(isDeletionBlockerResponseDto)
        && typeof value.revision === "string" && value.revision.length > 0;
}

export function isBackgroundDeletionResultResponseDto(value: unknown): value is BackgroundDeletionResultResponseDto {
    return isRecord(value) && isId(value.setId) && typeof value.setDeleted === "boolean" && isCount(value.items)
        && typeof value.cleanupPending === "boolean";
}
