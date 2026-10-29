import { isRecord } from "../../../transport/envelopes";
import { isCount, isId } from "../catalog-guards";

export type CatalogTargetKind = "variant" | "class" | "classVariant" | "asset";

const kinds: readonly string[] = ["variant", "class", "classVariant", "asset"] satisfies CatalogTargetKind[];

/**
 * Why deletion is unavailable: code (jobs.active, images.annotations, generation.preset_reference…), count and examples — names that
 * illustrate it. The line of the dialog is built by blockerText (lib/errors.ts).
 */
export interface DeletionBlockerResponseDto {
    code: string;
    count: number;
    examples: string[];
}

/**
 * The review of a permanent deletion: the counters "Classes / Class variants / Source images", blocking reasons and
 * revision, which goes to the deletion; when the contents change the deletion returns *.deletion_revision_conflict.
 * targetName is the name of the target (empty for an image).
 */
export interface DeletionReviewResponseDto {
    targetKind: CatalogTargetKind;
    targetId: string;
    targetName: string;
    classes: number;
    classVariants: number;
    assets: number;
    blockers: DeletionBlockerResponseDto[];
    revision: string;
}

export function isDeletionBlockerResponseDto(value: unknown): value is DeletionBlockerResponseDto {
    return isRecord(value) && typeof value.code === "string" && value.code.length > 0 && isCount(value.count)
        && Array.isArray(value.examples) && value.examples.every(item => typeof item === "string");
}

export function isDeletionReviewResponseDto(value: unknown): value is DeletionReviewResponseDto {
    return isRecord(value) && typeof value.targetKind === "string" && kinds.includes(value.targetKind)
        && isId(value.targetId) && typeof value.targetName === "string"
        && isCount(value.classes) && isCount(value.classVariants) && isCount(value.assets)
        && Array.isArray(value.blockers) && value.blockers.every(isDeletionBlockerResponseDto)
        && typeof value.revision === "string" && value.revision.length > 0;
}

/**
 * The result of a deletion: the rows are already deleted. cleanupPending: files are still being deleted in the background (many of them, or some in use):
 * "The material is deleted. Files are deleted in the background; if the application closes earlier, deletion continues the next time
 * the project opens."
 */
export interface DeletionResultResponseDto {
    classes: number;
    classVariants: number;
    assets: number;
    cleanupPending: boolean;
}

export function isDeletionResultResponseDto(value: unknown): value is DeletionResultResponseDto {
    return isRecord(value) && isCount(value.classes) && isCount(value.classVariants) && isCount(value.assets)
        && typeof value.cleanupPending === "boolean";
}
