/** The dataset split to evaluate on: val (Validation) | test (Test). */
export type EvaluationSplit = "val" | "test";

/**
 * Check on the dataset: device is "cpu" or a GPU index (as in training: deviceOptions), batch 1–1024 (16 by default);
 * operationId is new per action (a repeat returns the same evaluation).
 */
export interface StartModelEvaluationRequestDto {
    projectId: string;
    versionId: string;
    datasetVersionId: string;
    split: EvaluationSplit;
    device: string;
    batch: number | null;
    operationId: string;
}

export interface CancelModelEvaluationRequestDto {
    projectId: string;
    evaluationId: string;
}

export interface GetModelEvaluationRequestDto {
    projectId: string;
    evaluationId: string;
}

/** All checks, newest first; limit 1–200 (50 by default), before — the id of the last evaluation of the previous page. */
export interface ListModelEvaluationsRequestDto {
    projectId: string;
    versionId?: string;
    datasetVersionId?: string;
    split?: EvaluationSplit;
    limit?: number;
    before?: string;
}

/** The comparison matrix: the latest successful evaluation of each version on a "dataset · split" pair (without filters, all pairs). */
export interface ListLatestModelEvaluationsRequestDto {
    projectId: string;
    datasetVersionId?: string;
    split?: EvaluationSplit;
}

export interface SelectCheckImageRequestDto {
    projectId: string;
}

export interface SelectCheckVideoRequestDto {
    projectId: string;
}

/** Show another frame: the frame of the chosen video at timestampMs, scaled down to maxSide (1600 by default). */
export interface PreviewCheckVideoFrameRequestDto {
    projectId: string;
    selectionToken: string;
    timestampMs: number;
    maxSide?: number;
}

export type ListMonitorsRequestDto = Record<string, never>;

/** Take a screenshot. */
export interface CaptureScreenRequestDto {
    projectId: string;
    monitor: number;
}

/** Shared fields: confidence — the confidence threshold (null — 0.25), maxDetections — null for Auto, device — "cpu" or a GPU index. */
export interface CheckParametersDto {
    confidence: number | null;
    maxDetections: number | null;
    device: string;
}

/** The frame in frame pixels (for the screen, from the monitor corner), the size of the version input. */
export interface CheckRegionDto {
    x: number;
    y: number;
    width: number;
    height: number;
}

/** operationId is new per action; a repeat of the same request returns the same check. */
export interface StartImageCheckRequestDto {
    projectId: string;
    versionId: string;
    selectionToken: string;
    parameters: CheckParametersDto;
    operationId: string;
}

/** stepMs — the step (null — 1000 ms); region — null when the video is not larger than the input. */
export interface StartVideoCheckRequestDto {
    projectId: string;
    versionId: string;
    selectionToken: string;
    parameters: CheckParametersDto;
    stepMs: number | null;
    region: CheckRegionDto | null;
    operationId: string;
}

/** fps — frames per second (null — 3), maxDurationSec — stop after (null — 1800); region — null when the screen is not larger than the input. */
export interface StartScreenCheckRequestDto {
    projectId: string;
    versionId: string;
    monitor: number;
    parameters: CheckParametersDto;
    fps: number | null;
    maxDurationSec: number | null;
    region: CheckRegionDto | null;
    operationId: string;
}

export interface StopCheckRequestDto {
    projectId: string;
    checkId: string;
}

/** journalLimit 0–200 (40 by default): the last page of the journal in recentFrames. */
export interface GetCheckRequestDto {
    projectId: string;
    checkId: string;
    journalLimit?: number;
}

export interface ListChecksRequestDto {
    projectId: string;
}

/** after — read new records after an ordinal; before — page back; without cursors — the last limit records (1–200, 100 by default). */
export interface ListCheckJournalRequestDto {
    projectId: string;
    checkId: string;
    after?: number;
    before?: number;
    limit?: number;
}
