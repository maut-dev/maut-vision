import { isId, isRevision } from "../../projects/catalog/catalog-guards";
import { isRecord } from "../../transport/envelopes";

/** A changed version with its new revision; revision = null when it was deleted. */
export interface DatasetEntityChangeDto {
    id: string;
    revision: number | null;
}

/**
 * What changed in the datasets of the project: versions — versions with new revisions (built, renamed, archived or restored, integrity
 * verified) or deleted ones: read list / get again. Progress and outcomes of operations come with job.changed.
 */
export interface DatasetsChangedDto {
    projectId: string;
    versions: DatasetEntityChangeDto[];
}

export interface DatasetsChangedEventDto {
    kind: "event";
    event: "datasets.changed";
    data: DatasetsChangedDto;
}

export function isDatasetsChangedDto(value: unknown): value is DatasetsChangedDto {
    return isRecord(value) && typeof value.projectId === "string" && value.projectId.length > 0 && Array.isArray(value.versions)
        && value.versions.every(item => isRecord(item) && isId(item.id) && (item.revision === null || isRevision(item.revision)));
}
