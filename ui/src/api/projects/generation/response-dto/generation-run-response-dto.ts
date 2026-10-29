import { isJobResponseDto, type JobResponseDto } from "../../../jobs/response-dto/job-response-dto";
import { isRecord } from "../../../transport/envelopes";
import { isCount, isId } from "../../catalog/catalog-guards";
import { isGenerationRecipeResponseDto, type GenerationRecipeResponseDto } from "./generation-recipe-response-dto";

/** The kind of the generation run job: its snapshots arrive in job.changed. */
export const generationRunJobKind = "generation.run";

/**
 * The run result in job.result (a cancelled or failed run has it too): requestedCount, producedCount — published,
 * missingObjectFrames — "Frames with missing objects: N", outputGroupId — the result group (null until the run is prepared),
 * seed. Progress on the way — job.processed / job.total ("P / R images") and job.progress.
 */
export interface GenerationRunResultDto {
    requestedCount: number;
    producedCount: number;
    missingObjectFrames: number;
    outputGroupId: string | null;
    seed: number;
}

/**
 * A run: job — the snapshot of the job generation.run (status queued / running / succeeded / failed / cancelled / blocked, cancelRequested,
 * error by code, result); recipe — a copy of the preset ("Settings of this run"); seed; requestedCount; producedCount —
 * the published continuous prefix; missingObjectFrames; outputGroupId — "Open images" (null before preparation);
 * outputGroupName — "<preset> · <date>". Live progress — job.changed with kind generation.run and the same id.
 */
export interface GenerationRunResponseDto {
    job: JobResponseDto;
    recipe: GenerationRecipeResponseDto;
    seed: number;
    requestedCount: number;
    producedCount: number;
    missingObjectFrames: number;
    outputGroupId: string | null;
    outputGroupName: string;
}

/** A page of runs, newest first; total — "Runs (N)". */
export interface GenerationRunPageResponseDto {
    items: GenerationRunResponseDto[];
    total: number;
}

export interface GenerationRunDeletionResponseDto {
    runId: string;
}

export function isGenerationRunResultDto(value: unknown): value is GenerationRunResultDto {
    return isRecord(value) && isCount(value.requestedCount) && isCount(value.producedCount) && isCount(value.missingObjectFrames)
        && (value.outputGroupId === null || isId(value.outputGroupId)) && isCount(value.seed);
}

/** The run result from a job snapshot; null — a job of another kind or no result yet. */
export function parseGenerationRunResult(job: JobResponseDto): GenerationRunResultDto | null {
    return job.kind === generationRunJobKind && isGenerationRunResultDto(job.result) ? job.result : null;
}

export function isGenerationRunResponseDto(value: unknown): value is GenerationRunResponseDto {
    return isRecord(value) && isJobResponseDto(value.job) && value.job.kind === generationRunJobKind && isGenerationRecipeResponseDto(value.recipe)
        && isCount(value.seed) && isCount(value.requestedCount) && isCount(value.producedCount) && isCount(value.missingObjectFrames)
        && (value.outputGroupId === null || isId(value.outputGroupId)) && typeof value.outputGroupName === "string";
}

export function isGenerationRunPageResponseDto(value: unknown): value is GenerationRunPageResponseDto {
    return isRecord(value) && Array.isArray(value.items) && value.items.every(isGenerationRunResponseDto) && isCount(value.total);
}

export function isGenerationRunDeletionResponseDto(value: unknown): value is GenerationRunDeletionResponseDto {
    return isRecord(value) && isId(value.runId);
}
