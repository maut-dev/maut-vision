import { isId } from "../../projects/catalog/catalog-guards";
import { isRecord } from "../../transport/envelopes";

/** A changed run or version with its new revision; revision = null when it was deleted. */
export interface TrainingEntityChangeDto {
    id: string;
    revision: number | null;
}

/**
 * What changed in the training of the project: runs — runs (progress, epochs, log, actual parameters, outcome; at most once per 500 ms):
 * read getRun again when the revision is greater than the known one, then listEpochs / listLog with after; versions — model versions (new,
 * the working one changed, deleted — revision = null together with its evaluations): read listModelVersions again. The job state and progress come with job.changed (kind training.run).
 */
export interface TrainingChangedDto {
    projectId: string;
    runs: TrainingEntityChangeDto[];
    versions: TrainingEntityChangeDto[];
}

export interface TrainingChangedEventDto {
    kind: "event";
    event: "training.changed";
    data: TrainingChangedDto;
}

/** Training presets were saved or deleted: read listPresets again. */
export interface TrainingPresetsChangedDto {
    presetIds: string[];
}

export interface TrainingPresetsChangedEventDto {
    kind: "event";
    event: "trainingPresets.changed";
    data: TrainingPresetsChangedDto;
}

const isChange = (item: unknown) => isRecord(item) && isId(item.id) && (item.revision === null || (Number.isSafeInteger(item.revision) && (item.revision as number) >= 0));

export function isTrainingChangedDto(value: unknown): value is TrainingChangedDto {
    return isRecord(value) && typeof value.projectId === "string" && value.projectId.length > 0 && Array.isArray(value.runs) && value.runs.every(isChange)
        && Array.isArray(value.versions) && value.versions.every(isChange);
}

export function isTrainingPresetsChangedDto(value: unknown): value is TrainingPresetsChangedDto {
    return isRecord(value) && Array.isArray(value.presetIds) && value.presetIds.every(isId);
}
