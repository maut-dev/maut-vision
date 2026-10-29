import { isId, isRevision } from "../../projects/catalog/catalog-guards";
import { isRecord } from "../../transport/envelopes";

/** A changed preset with its new revision; revision = null when it was deleted. */
export interface GenerationEntityChangeDto {
    id: string;
    revision: number | null;
}

/**
 * What changed in the generation of the project: recipes — presets with new revisions or deleted ones; runs — runs whose data changed
 * besides the job state (prepared — the result group appeared; deleted): read their getRun again (generation.unavailable means it
 * was deleted). The state and progress of runs come with job.changed.
 */
export interface GenerationChangedDto {
    projectId: string;
    recipes: GenerationEntityChangeDto[];
    runs: string[];
}

export interface GenerationChangedEventDto {
    kind: "event";
    event: "generation.changed";
    data: GenerationChangedDto;
}

function isChange(value: unknown): value is GenerationEntityChangeDto {
    return isRecord(value) && isId(value.id) && (value.revision === null || isRevision(value.revision));
}

export function isGenerationChangedDto(value: unknown): value is GenerationChangedDto {
    return isRecord(value) && typeof value.projectId === "string" && value.projectId.length > 0
        && Array.isArray(value.recipes) && value.recipes.every(isChange) && Array.isArray(value.runs) && value.runs.every(isId);
}
