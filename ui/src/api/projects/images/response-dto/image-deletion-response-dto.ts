import { isRecord } from "../../../transport/envelopes";
import { isCount, isId } from "../../catalog/catalog-guards";
import type { DeletionBlockerResponseDto } from "../../catalog/response-dto/deletion-review-response-dto";

/**
 * Состав удаления навсегда: вся группа (wholeGroup) или выбранные изображения. «Изображений к удалению: N» — images;
 * непустой blockers — «Удаление пока недоступно:» + строки message (images.import_active — импорт в эту группу; позже —
 * генерация). revision передаётся в удаление; при изменении состава — images.deletion_revision_conflict.
 */
export interface ImageDeletionReviewResponseDto {
    groupId: string;
    groupName: string;
    wholeGroup: boolean;
    images: number;
    blockers: DeletionBlockerResponseDto[];
    revision: string;
}

/** Строки удалены; cleanupPending — файлы ещё удаляются в фоне, очистка продолжится при следующем открытии проекта. */
export interface ImageDeletionResultResponseDto {
    groupId: string;
    groupDeleted: boolean;
    images: number;
    cleanupPending: boolean;
}

function isBlocker(value: unknown): value is DeletionBlockerResponseDto {
    return isRecord(value) && typeof value.code === "string" && value.code.length > 0 && isCount(value.count)
        && typeof value.message === "string" && value.message.length > 0;
}

export function isImageDeletionReviewResponseDto(value: unknown): value is ImageDeletionReviewResponseDto {
    return isRecord(value) && isId(value.groupId) && typeof value.groupName === "string" && typeof value.wholeGroup === "boolean"
        && isCount(value.images) && Array.isArray(value.blockers) && value.blockers.every(isBlocker)
        && typeof value.revision === "string" && value.revision.length > 0;
}

export function isImageDeletionResultResponseDto(value: unknown): value is ImageDeletionResultResponseDto {
    return isRecord(value) && isId(value.groupId) && typeof value.groupDeleted === "boolean" && isCount(value.images)
        && typeof value.cleanupPending === "boolean";
}
