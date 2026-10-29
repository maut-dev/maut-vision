import type { JobResponseDto } from "@/api/jobs/response-dto/job-response-dto";
import { generationRunSummary, generationTexts } from "@/api/projects/generation/generation-rules";
import { parseGenerationRunResult, type GenerationRunResponseDto, type GenerationRunResultDto } from "@/api/projects/generation/response-dto/generation-run-response-dto";
import { i18n } from "@/i18n";
import { jobErrorText } from "@/lib/errors";

/** The run is still going: queued, running or waiting for resources — it can be cancelled but not deleted. */
export function isRunActive(job: JobResponseDto): boolean {
    return job.status === "queued" || job.status === "running" || job.status === "blocked";
}

/**
 * Figures of the run by the last job snapshot: published (the job result, otherwise processed, otherwise the stored value of the
 * run), the result group and frames with missing objects.
 */
export function runProgress(run: GenerationRunResponseDto): GenerationRunResultDto & { percent: number } {
    const result = parseGenerationRunResult(run.job);
    const producedCount = result?.producedCount ?? Math.max(run.producedCount, run.job.processed ?? 0);
    const requestedCount = result?.requestedCount ?? run.requestedCount;
    const summary = {
        requestedCount,
        producedCount,
        missingObjectFrames: result?.missingObjectFrames ?? run.missingObjectFrames,
        outputGroupId: result?.outputGroupId ?? run.outputGroupId,
        seed: run.seed,
    };
    return { ...summary, percent: requestedCount === 0 ? 0 : Math.floor(100 * producedCount / requestedCount) };
}

/** "P / R images · X%" and the result "Frames with missing objects: N". */
export function runSummary(run: GenerationRunResponseDto): string {
    return generationRunSummary(runProgress(run));
}

/**
 * The error text of a finished run (by its code) and a tail about saved images. null — the run succeeded or is still going.
 */
export function runErrorText(run: GenerationRunResponseDto): string | null {
    const { job } = run;
    if (isRunActive(job) || job.status === "succeeded") return null;
    const message = (job.error ? jobErrorText(job.error) : null)
        ?? (job.status === "cancelled" ? generationTexts.cancelled : job.status === "interrupted" ? generationTexts.interrupted : null);
    if (!message) return null;
    const produced = runProgress(run).producedCount;
    return i18n.t(produced > 0 ? "generation:runs.errorSaved" : "generation:runs.errorNothingSaved", { message });
}
