import type { JobResponseDto } from "../../../jobs/response-dto/job-response-dto";
import { isRecord } from "../../../transport/envelopes";
import { isCount } from "../catalog-guards";

/** Вид задачи импорта изображений варианта класса. */
export const importClassAssetsJobKind = "classes.import_assets";

/** Отклонённый файл: имя без папки, код причины (classes.asset_invalid, classes.asset_unreadable) и текст для показа. */
export interface ImportRejectedFileDto {
    name: string;
    reason: string;
}

/**
 * Итог импорта в job.result: всего файлов, добавлено, пропущено одинаковых, отклонено; rejectedFiles — первые 50
 * отклонённых. Итог есть и у отменённой задачи: добавленное до отмены сохраняется.
 */
export interface ImportClassAssetsResultDto {
    total: number;
    added: number;
    duplicates: number;
    rejected: number;
    rejectedFiles: ImportRejectedFileDto[];
}

function isImportRejectedFileDto(value: unknown): value is ImportRejectedFileDto {
    return isRecord(value) && typeof value.name === "string" && typeof value.reason === "string";
}

export function isImportClassAssetsResultDto(value: unknown): value is ImportClassAssetsResultDto {
    return isRecord(value) && isCount(value.total) && isCount(value.added) && isCount(value.duplicates) && isCount(value.rejected)
        && Array.isArray(value.rejectedFiles) && value.rejectedFiles.every(isImportRejectedFileDto);
}

/** Итог импорта из снимка задачи; null — задача другого вида или итога ещё нет. */
export function parseImportClassAssetsResult(job: JobResponseDto): ImportClassAssetsResultDto | null {
    return job.kind === importClassAssetsJobKind && isImportClassAssetsResultDto(job.result) ? job.result : null;
}
