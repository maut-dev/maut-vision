/** Runs, newest first; limit 1–200 (50 by default), before — the ID of the last run of the previous page. */
export interface ListGenerationRunsRequestDto {
    projectId: string;
    limit?: number;
    before?: string;
}

export interface GetGenerationRunRequestDto {
    projectId: string;
    runId: string;
}

/**
 * "Generate N images": recipeRevision — the preset revision the user saw; seed — 0–4 294 967 295; count ≥ 1;
 * operationId — new for each user action (a repeat of the request with the same operationId returns the same run).
 */
export interface StartGenerationRunRequestDto {
    projectId: string;
    recipeId: string;
    recipeRevision: number;
    seed: number;
    count: number;
    operationId: string;
}

/** Deletes a finished run; the images of the result group stay. */
export interface DeleteGenerationRunRequestDto {
    projectId: string;
    runId: string;
}

/** "Set up a repeat". */
export interface GetGenerationRepeatDraftRequestDto {
    projectId: string;
    runId: string;
}
