import { isJobResponseDto, type JobResponseDto } from "../../../jobs/response-dto/job-response-dto";
import { isRecord } from "../../../transport/envelopes";
import { isCount, isId } from "../../catalog/catalog-guards";
import { isGenerationRecipeResponseDto, type GenerationRecipeResponseDto } from "./generation-recipe-response-dto";

/** Вид задачи запуска генерации: её снимки приходят в job.changed. */
export const generationRunJobKind = "generation.run";

/**
 * Итог запуска в job.result (есть и у отменённого или неудачного): requestedCount, producedCount — опубликовано,
 * missingObjectFrames — «Кадров с недостающими объектами: N», outputGroupId — группа результата (null, пока запуск не подготовлен),
 * seed. Прогресс по ходу — job.processed / job.total («P / R изображений») и job.progress.
 */
export interface GenerationRunResultDto {
    requestedCount: number;
    producedCount: number;
    missingObjectFrames: number;
    outputGroupId: string | null;
    seed: number;
}

/**
 * Запуск: job — снимок задачи generation.run (status queued / running / succeeded / failed / cancelled / blocked, cancelRequested,
 * error с русским текстом, result); recipe — копия пресета («Настройки этого запуска»); seed; requestedCount; producedCount —
 * опубликованный непрерывный префикс; missingObjectFrames; outputGroupId — «Открыть изображения» (null до подготовки);
 * outputGroupName — «<пресет> · <дата>». Живой прогресс — job.changed с kind generation.run и тем же id.
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

/** Страница запусков, новые первыми; total — «Запуски (N)». */
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

/** Итог запуска из снимка задачи; null — задача другого вида или итога ещё нет. */
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
