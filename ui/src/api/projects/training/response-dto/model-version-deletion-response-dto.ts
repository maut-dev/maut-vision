import { isRecord } from "../../../transport/envelopes";
import { isCount, isId } from "../../catalog/catalog-guards";
import { isDeletionBlockerResponseDto, type DeletionBlockerResponseDto } from "../../catalog/response-dto/deletion-review-response-dto";

/**
 * What deleting a version removes: evaluations — its evaluations on datasets (deleted with it), bytes — the size of its files; a non-empty
 * blockers means deletion is unavailable (training.version_working — the working version, checks.evaluation_active, checks.live_active,
 * training.export_active; lines — blockerText).
 */
export interface ModelVersionDeletionReviewResponseDto {
    versionId: string;
    number: number;
    isWorking: boolean;
    evaluations: number;
    bytes: number;
    blockers: DeletionBlockerResponseDto[];
}

/** The version was deleted; cleanupPending — its file folder is still being deleted in the background. */
export interface ModelVersionDeletionResponseDto {
    versionId: string;
    number: number;
    evaluations: number;
    cleanupPending: boolean;
}

/** token — the chosen save location (null — the dialog was closed); fileName — the name of the chosen file. */
export interface ModelExportSelectionResponseDto {
    token: string | null;
    fileName: string | null;
}

/** The result of the job training.export (job.result): "Version N exported: model-vN.zip · 1.2 MB". */
export interface ModelExportResultDto {
    versionId: string;
    versionNumber: number;
    fileName: string;
    bytes: number;
    sha256: string | null;
}

/** The kind of the ZIP export job: its snapshots and result arrive in job.changed. */
export const modelExportJobKind = "training.export";

export function isModelVersionDeletionReviewResponseDto(value: unknown): value is ModelVersionDeletionReviewResponseDto {
    return isRecord(value) && isId(value.versionId) && isCount(value.number) && typeof value.isWorking === "boolean" && isCount(value.evaluations)
        && isCount(value.bytes) && Array.isArray(value.blockers) && value.blockers.every(isDeletionBlockerResponseDto);
}

export function isModelVersionDeletionResponseDto(value: unknown): value is ModelVersionDeletionResponseDto {
    return isRecord(value) && isId(value.versionId) && isCount(value.number) && isCount(value.evaluations) && typeof value.cleanupPending === "boolean";
}

export function isModelExportSelectionResponseDto(value: unknown): value is ModelExportSelectionResponseDto {
    return isRecord(value) && (value.token === null ? value.fileName === null : typeof value.token === "string" && value.token.length > 0
        && typeof value.fileName === "string");
}

export function isModelExportResultDto(value: unknown): value is ModelExportResultDto {
    return isRecord(value) && isId(value.versionId) && isCount(value.versionNumber) && typeof value.fileName === "string" && isCount(value.bytes)
        && (value.sha256 === null || (typeof value.sha256 === "string" && /^[0-9a-f]{64}$/.test(value.sha256)));
}
