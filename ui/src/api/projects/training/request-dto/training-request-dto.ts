/** A training parameter value: a number, a string, a flag, null ("Auto") or a list (classes, freeze, fraction, scale). */
export type TrainingParameterValue = number | string | boolean | null | (number | string)[];

/** Parameters: worker protocol keys (see training-parameters.ts); a missing key is the tuned default. */
export type TrainingParameters = Record<string, TrainingParameterValue>;

export type ListTrainingPresetsRequestDto = Record<string, never>;

/** "Save as preset…": the same name (ignoring case) overwrites the preset. */
export interface SaveTrainingPresetRequestDto {
    name: string;
    parameters: TrainingParameters;
}

export interface DeleteTrainingPresetRequestDto {
    presetId: string;
}

/** refresh — ask nvidia-smi again (by default the answer is cached). */
export interface ListGpusRequestDto {
    refresh?: boolean;
}

export type GetTrainingRuntimeRequestDto = Record<string, never>;

/** "Start training": operationId — new per action; a repeat with the same operationId returns the same run. */
export interface StartTrainingRunRequestDto {
    projectId: string;
    datasetVersionId: string;
    parameters: TrainingParameters;
    presetName: string | null;
    operationId: string;
}

export interface CancelTrainingRunRequestDto {
    projectId: string;
    runId: string;
}

/** limit 1–200 (50 by default); before — the id of the last run of the previous page. */
export interface ListTrainingRunsRequestDto {
    projectId: string;
    limit?: number;
    before?: string;
}

export interface GetTrainingRunRequestDto {
    projectId: string;
    runId: string;
}

/** after — the number of the last received epoch; limit 1–1000 (500 by default). */
export interface ListTrainingEpochsRequestDto {
    projectId: string;
    runId: string;
    after?: number;
    limit?: number;
}

/** after — the sequence of the last received entry; limit 1–1000 (200 by default). */
export interface ListTrainingLogRequestDto {
    projectId: string;
    runId: string;
    after?: number;
    limit?: number;
}

/** The last log entries, limit 1–1000 (50 by default). */
export interface TailTrainingLogRequestDto {
    projectId: string;
    runId: string;
    limit?: number;
}

export interface ListModelVersionsRequestDto {
    projectId: string;
}

export interface GetModelVersionRequestDto {
    projectId: string;
    versionId: string;
}

export interface SetWorkingModelVersionRequestDto {
    projectId: string;
    versionId: string;
}

/** What deleting a version removes, for the shared deletion dialog. */
export interface ReviewModelVersionDeletionRequestDto {
    projectId: string;
    versionId: string;
}

/** "Delete version": a working version or one under a check or an export cannot be deleted (training.version_in_use, details.reason). */
export interface DeleteModelVersionRequestDto {
    projectId: string;
    versionId: string;
}

/** "Export ZIP", step 1: the native "Save as" with the name model-vN.zip. */
export interface SelectModelExportRequestDto {
    projectId: string;
    versionId: string;
}

/** "Export ZIP", step 2: destinationToken — from selectModelExportDestination; operationId — new per action. */
export interface StartModelExportRequestDto {
    projectId: string;
    versionId: string;
    destinationToken: string;
    operationId: string;
}
