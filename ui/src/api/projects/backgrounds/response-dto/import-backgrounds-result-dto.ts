import type { JobResponseDto } from "../../../jobs/response-dto/job-response-dto";
import { isRecord } from "../../../transport/envelopes";
import { isCount } from "../../catalog/catalog-guards";
import { isInputSizeDto, type InputSizeDto } from "../../input-size-dto";
import { isBackgroundBlurResponseDto, type BackgroundBlurResponseDto } from "./background-import-preview-response-dto";

/** Вид задачи подготовки фонов. */
export const importBackgroundsJobKind = "backgrounds.import";

/**
 * Причина пропуска («Причины пропуска»): sourceIndex — место источника в запуске (у видео — номер кадра), name — имя файла или
 * видео, reason — код, message — текст («Размер a×b, нужен W×H», «Рамка W×H не помещается в кадр a×b», «Не удалось прочитать
 * изображение», «Почти не отличается от уже взятого кадра», «Такой кадр уже есть в наборе»), width/height — размер файла или
 * кадра, timestampMs — кадр видео.
 */
export interface BackgroundImportIssueDto {
    sourceIndex: number;
    name: string;
    reason: string;
    width: number | null;
    height: number | null;
    timestampMs: number | null;
}

/**
 * Итог подготовки в job.result (есть и у отменённой, прерванной или неудачной задачи): total — источников (файлов или кадров),
 * processed — рассмотрено («обработано / всего»), added, duplicates (точные повторы), similar (почти одинаковые), rejected;
 * requestedCount — цель запуска («из N»); targetSize — размер набора; blur — с выбранным seed или null; issues — первые 100.
 * Прогресс по ходу — job.progress, job.processed и job.total.
 */
export interface ImportBackgroundsResultDto {
    total: number;
    processed: number;
    added: number;
    duplicates: number;
    similar: number;
    rejected: number;
    requestedCount: number | null;
    targetSize: InputSizeDto;
    blur: BackgroundBlurResponseDto | null;
    issues: BackgroundImportIssueDto[];
}

const isCountOrNull = (value: unknown) => value === null || isCount(value);

function isIssue(value: unknown): value is BackgroundImportIssueDto {
    return isRecord(value) && isCount(value.sourceIndex) && typeof value.name === "string" && typeof value.reason === "string"
        && isCountOrNull(value.width) && isCountOrNull(value.height) && isCountOrNull(value.timestampMs);
}

export function isImportBackgroundsResultDto(value: unknown): value is ImportBackgroundsResultDto {
    return isRecord(value) && isCount(value.total) && isCount(value.processed) && isCount(value.added) && isCount(value.duplicates)
        && isCount(value.similar) && isCount(value.rejected) && isCountOrNull(value.requestedCount) && isInputSizeDto(value.targetSize)
        && (value.blur === null || isBackgroundBlurResponseDto(value.blur)) && Array.isArray(value.issues) && value.issues.every(isIssue);
}

/** Итог подготовки из снимка задачи; null — задача другого вида или итога ещё нет. */
export function parseImportBackgroundsResult(job: JobResponseDto): ImportBackgroundsResultDto | null {
    return job.kind === importBackgroundsJobKind && isImportBackgroundsResultDto(job.result) ? job.result : null;
}
