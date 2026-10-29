import { isRecord } from "../../../transport/envelopes";
import { isCount, isDate, isDateOrNull, isId, isRevision } from "../../catalog/catalog-guards";
import { isInputSizeDto, type InputSizeDto } from "../../input-size-dto";

/**
 * A background set: "N backgrounds · W × H[ · Archived]" from itemCount (active ones) and targetSize. matchesProjectInput is false when the
 * project input changed after creation: "The set size W × H differs from the project input" (such sets do not fit generation
 * of the new size). revision is for updateSet and setSetArchived; contentRevision grows with every change of the contents;
 * removedCount counts removed backgrounds. Preparation changes only the counters (backgrounds.changed, itemsChangedIn).
 */
export interface BackgroundSetResponseDto {
    id: string;
    name: string;
    description: string;
    targetSize: InputSizeDto;
    revision: number;
    contentRevision: number;
    itemCount: number;
    removedCount: number;
    createdAt: string;
    updatedAt: string;
    archivedAt: string | null;
    matchesProjectInput: boolean;
}

export function isBackgroundSetResponseDto(value: unknown): value is BackgroundSetResponseDto {
    return isRecord(value) && isId(value.id) && typeof value.name === "string" && typeof value.description === "string"
        && isInputSizeDto(value.targetSize) && isRevision(value.revision) && isRevision(value.contentRevision)
        && isCount(value.itemCount) && isCount(value.removedCount) && isDate(value.createdAt) && isDate(value.updatedAt)
        && isDateOrNull(value.archivedAt) && typeof value.matchesProjectInput === "boolean";
}

export function isBackgroundSetList(value: unknown): value is BackgroundSetResponseDto[] {
    return Array.isArray(value) && value.every(isBackgroundSetResponseDto);
}
