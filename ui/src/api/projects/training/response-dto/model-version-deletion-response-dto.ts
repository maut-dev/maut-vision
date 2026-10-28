import { isRecord } from "../../../transport/envelopes";
import { isCount, isId } from "../../catalog/catalog-guards";
import { isDeletionBlockerResponseDto, type DeletionBlockerResponseDto } from "../../catalog/response-dto/deletion-review-response-dto";

/**
 * Состав удаления версии: evaluations — её оценок на датасетах (удаляются вместе с ней), bytes — размер файлов; непустой blockers —
 * удаление недоступно (training.version_working — рабочая версия, checks.evaluation_active, checks.live_active,
 * training.export_active; строки — message).
 */
export interface ModelVersionDeletionReviewResponseDto {
    versionId: string;
    number: number;
    isWorking: boolean;
    evaluations: number;
    bytes: number;
    blockers: DeletionBlockerResponseDto[];
}

/** Версия удалена; cleanupPending — папка файлов ещё удаляется в фоне. */
export interface ModelVersionDeletionResponseDto {
    versionId: string;
    number: number;
    evaluations: number;
    cleanupPending: boolean;
}

/** token — выбор места сохранения (null — окно закрыто); fileName — имя выбранного файла. */
export interface ModelExportSelectionResponseDto {
    token: string | null;
    fileName: string | null;
}

/** Итог задачи training.export (job.result): «Версия N выгружена: model-vN.zip · 1,2 МБ». */
export interface ModelExportResultDto {
    versionId: string;
    versionNumber: number;
    fileName: string;
    bytes: number;
    sha256: string | null;
}

/** Вид задачи выгрузки ZIP: её снимки и итог приходят в job.changed. */
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
