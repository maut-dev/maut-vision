import { isId, isRevision } from "../../projects/catalog/catalog-guards";
import { isRecord } from "../../transport/envelopes";

/** A changed set or background with its new revision; revision = null when it was deleted. */
export interface BackgroundsEntityChangeDto {
    id: string;
    revision: number | null;
}

/**
 * What changed in the backgrounds of the project. sets — sets with new revisions or deleted ones; items — backgrounds changed by an explicit
 * operation (remove, restore, deleting the chosen ones); itemsChangedIn — sets whose content or counters changed (preparation reports at
 * most once per 500 ms per set): the UI reads their set and the loaded pages again.
 */
export interface BackgroundsChangedDto {
    projectId: string;
    sets: BackgroundsEntityChangeDto[];
    items: BackgroundsEntityChangeDto[];
    itemsChangedIn: string[];
}

export interface BackgroundsChangedEventDto {
    kind: "event";
    event: "backgrounds.changed";
    data: BackgroundsChangedDto;
}

function isChange(value: unknown): value is BackgroundsEntityChangeDto {
    return isRecord(value) && isId(value.id) && (value.revision === null || isRevision(value.revision));
}

export function isBackgroundsChangedDto(value: unknown): value is BackgroundsChangedDto {
    return isRecord(value) && typeof value.projectId === "string" && value.projectId.length > 0
        && Array.isArray(value.sets) && value.sets.every(isChange) && Array.isArray(value.items) && value.items.every(isChange)
        && Array.isArray(value.itemsChangedIn) && value.itemsChangedIn.every(isId);
}
