import type { JobResponseDto } from "@/api/jobs/response-dto/job-response-dto";
import { generationRunSummary, generationTexts } from "@/api/projects/generation/generation-rules";
import { parseGenerationRunResult, type GenerationRunResponseDto, type GenerationRunResultDto } from "@/api/projects/generation/response-dto/generation-run-response-dto";

/** Запуск ещё идёт: в очереди, выполняется или ожидает ресурсов — его можно отменить, но не удалить. */
export function isRunActive(job: JobResponseDto): boolean {
    return job.status === "queued" || job.status === "running" || job.status === "blocked";
}

/**
 * Показатели запуска по последнему снимку задачи: опубликовано (итог задачи, иначе processed, иначе сохранённое
 * значение запуска), группа результата и кадры с недостающими объектами.
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

/** «P / R изображений · X%» и итог «Кадров с недостающими объектами: N». */
export function runSummary(run: GenerationRunResponseDto): string {
    return generationRunSummary(runProgress(run));
}

/**
 * Текст ошибки завершённого запуска (тексты host) и хвост о сохранённых изображениях. null — запуск успешен или ещё идёт.
 */
export function runErrorText(run: GenerationRunResponseDto): string | null {
    const { job } = run;
    if (isRunActive(job) || job.status === "succeeded") return null;
    const message = job.error?.message
        ?? (job.status === "cancelled" ? generationTexts.cancelled : job.status === "interrupted" ? generationTexts.interrupted : null);
    if (!message) return null;
    const produced = runProgress(run).producedCount;
    return `${message} ${produced > 0 ? "Если группа результата сохранилась, в ней доступны созданные изображения." : "Изображения не были сохранены."}`;
}
