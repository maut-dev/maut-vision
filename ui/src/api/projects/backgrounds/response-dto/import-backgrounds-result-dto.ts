import type { JobResponseDto } from "../../../jobs/response-dto/job-response-dto";
import { isRecord } from "../../../transport/envelopes";
import { isCount } from "../../catalog/catalog-guards";
import { isInputSizeDto, type InputSizeDto } from "../../input-size-dto";
import { isBackgroundBlurResponseDto, type BackgroundBlurResponseDto } from "./background-import-preview-response-dto";

/** The job kind of background preparation. */
export const importBackgroundsJobKind = "backgrounds.import";

/**
 * A skip reason ("Skip reasons"): sourceIndex is the place of the source in the run (for a video the frame number), name the name of the file or
 * the video, reason a code, message a text ("Size a×b, W×H needed", "The W×H frame does not fit into a×b", "Could not read
 * the image", "Almost the same as a shot already taken", "This frame is already in the set"), width/height the size of the file or
 * the frame, timestampMs the video frame.
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
 * The preparation result in job.result (a cancelled, interrupted or failed job has it too): total counts sources (files or frames),
 * processed the considered ones ("processed / total"), added, duplicates (exact repeats), similar (almost identical), rejected;
 * requestedCount is the target of the run ("of N"); targetSize the set size; blur with the chosen seed or null; issues the first 100.
 * Progress along the way is job.progress, job.processed and job.total.
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

/** The preparation result from a job snapshot; null for a job of another kind or without a result yet. */
export function parseImportBackgroundsResult(job: JobResponseDto): ImportBackgroundsResultDto | null {
    return job.kind === importBackgroundsJobKind && isImportBackgroundsResultDto(job.result) ? job.result : null;
}
