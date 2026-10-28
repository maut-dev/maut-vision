import type { JobResponseDto } from "../../../jobs/response-dto/job-response-dto";
import { isRecord } from "../../../transport/envelopes";
import { isCount } from "../../catalog/catalog-guards";
import { isImportClassAssetsResultDto, type ImportRejectedFileDto } from "../../catalog/response-dto/import-class-assets-result-dto";

/** Вид задачи импорта изображений в группу. */
export const importImagesJobKind = "images.import";

/**
 * Итог импорта в job.result: total — файлов (кадров) после раскрытия папок и видео, added, duplicates («пропущено» — повторы в группе),
 * rejected; rejectedFiles — первые 50 отклонённых (имя или путь внутри папки, код images.file_invalid, video.frame_unavailable или
 * images.file_unreadable и текст). Итог есть и у отменённой или прерванной задачи: добавленное сохраняется.
 */
export interface ImportImagesResultDto {
    total: number;
    added: number;
    duplicates: number;
    rejected: number;
    rejectedFiles: ImportRejectedFileDto[];
}

export function isImportImagesResultDto(value: unknown): value is ImportImagesResultDto {
    return isRecord(value) && isCount(value.total) && isImportClassAssetsResultDto(value);
}

/** Итог импорта изображений из снимка задачи; null — задача другого вида или итога ещё нет. */
export function parseImportImagesResult(job: JobResponseDto): ImportImagesResultDto | null {
    return job.kind === importImagesJobKind && isImportImagesResultDto(job.result) ? job.result : null;
}
