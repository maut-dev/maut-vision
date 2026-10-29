import type { JobResponseDto } from "../../../jobs/response-dto/job-response-dto";
import { isRecord } from "../../../transport/envelopes";
import { isCount } from "../catalog-guards";

/** The job kind of an image import into a class variant. */
export const importClassAssetsJobKind = "classes.import_assets";

/** A rejected file: the name without a folder, the reason code (classes.asset_invalid, classes.asset_unreadable) and a text to show. */
export interface ImportRejectedFileDto {
    name: string;
    reason: string;
}

/**
 * The import result in job.result: total files, added, identical skipped, rejected; rejectedFiles are the first 50
 * rejected ones. A cancelled job has a result too: what was added before cancelling is kept.
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

/** The import result from a job snapshot; null for a job of another kind or without a result yet. */
export function parseImportClassAssetsResult(job: JobResponseDto): ImportClassAssetsResultDto | null {
    return job.kind === importClassAssetsJobKind && isImportClassAssetsResultDto(job.result) ? job.result : null;
}
