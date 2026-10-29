import { isId, isRevision } from "../../projects/catalog/catalog-guards";
import { isRecord } from "../../transport/envelopes";

/** A changed group or image with its new revision; revision = null when it was deleted. */
export interface ImagesEntityChangeDto {
    id: string;
    revision: number | null;
}

/**
 * What changed in the images of the project. groups — groups with new revisions or deleted ones; images — images changed by an explicit
 * operation (annotation, archive, moving, deleting the chosen ones); imagesChangedIn — groups whose content or counters changed
 * (an import reports at most once per 500 ms per group): the UI reads their group and the loaded pages again.
 */
export interface ImagesChangedDto {
    projectId: string;
    groups: ImagesEntityChangeDto[];
    images: ImagesEntityChangeDto[];
    imagesChangedIn: string[];
}

export interface ImagesChangedEventDto {
    kind: "event";
    event: "images.changed";
    data: ImagesChangedDto;
}

function isChange(value: unknown): value is ImagesEntityChangeDto {
    return isRecord(value) && isId(value.id) && (value.revision === null || isRevision(value.revision));
}

export function isImagesChangedDto(value: unknown): value is ImagesChangedDto {
    return isRecord(value) && typeof value.projectId === "string" && value.projectId.length > 0
        && Array.isArray(value.groups) && value.groups.every(isChange) && Array.isArray(value.images) && value.images.every(isChange)
        && Array.isArray(value.imagesChangedIn) && value.imagesChangedIn.every(isId);
}
