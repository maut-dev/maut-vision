import type { JobResponseDto } from "../../../jobs/response-dto/job-response-dto";
import { isRecord } from "../../../transport/envelopes";
import { isCount } from "../../catalog/catalog-guards";
import { isImportClassAssetsResultDto, type ImportRejectedFileDto } from "../../catalog/response-dto/import-class-assets-result-dto";

/** The job kind of an image import into a group. */
export const importImagesJobKind = "images.import";

/**
 * The import result in job.result: total counts files (frames) after expanding folders and videos, added, duplicates ("skipped": duplicates in the group),
 * rejected; rejectedFiles are the first 50 rejected ones (a name or a path inside the folder, the code images.file_invalid, video.frame_unavailable or
 * images.file_unreadable, and a text). A cancelled or interrupted job has a result too: what was added is kept.
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

/** The image import result from a job snapshot; null for a job of another kind or without a result yet. */
export function parseImportImagesResult(job: JobResponseDto): ImportImagesResultDto | null {
    return job.kind === importImagesJobKind && isImportImagesResultDto(job.result) ? job.result : null;
}
