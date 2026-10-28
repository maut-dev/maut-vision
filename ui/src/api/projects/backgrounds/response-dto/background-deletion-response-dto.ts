import { isRecord } from "../../../transport/envelopes";
import { isCount, isId } from "../../catalog/catalog-guards";
import type { DeletionBlockerResponseDto } from "../../catalog/response-dto/deletion-review-response-dto";

/**
 * Состав удаления навсегда: весь набор (wholeSet — «Удалить набор фонов навсегда?») или выбранные фоны («Удалить фоны
 * навсегда?»). items — «Будет удалено фонов: N.»; непустой blockers — «Завершите задачи или измените зависимости перед
 * удалением:» + строки message (backgrounds.import_active — подготовка в этот набор; пресеты генерации).
 * revision передаётся в удаление; при изменении состава — backgrounds.deletion_revision_conflict.
 */
export interface BackgroundDeletionReviewResponseDto {
    setId: string;
    setName: string;
    wholeSet: boolean;
    items: number;
    blockers: DeletionBlockerResponseDto[];
    revision: string;
}

/** Строки удалены; cleanupPending — файлы ещё удаляются в фоне, очистка продолжится при следующем открытии проекта. */
export interface BackgroundDeletionResultResponseDto {
    setId: string;
    setDeleted: boolean;
    items: number;
    cleanupPending: boolean;
}

function isBlocker(value: unknown): value is DeletionBlockerResponseDto {
    return isRecord(value) && typeof value.code === "string" && value.code.length > 0 && isCount(value.count)
        && typeof value.message === "string" && value.message.length > 0;
}

export function isBackgroundDeletionReviewResponseDto(value: unknown): value is BackgroundDeletionReviewResponseDto {
    return isRecord(value) && isId(value.setId) && typeof value.setName === "string" && typeof value.wholeSet === "boolean"
        && isCount(value.items) && Array.isArray(value.blockers) && value.blockers.every(isBlocker)
        && typeof value.revision === "string" && value.revision.length > 0;
}

export function isBackgroundDeletionResultResponseDto(value: unknown): value is BackgroundDeletionResultResponseDto {
    return isRecord(value) && isId(value.setId) && typeof value.setDeleted === "boolean" && isCount(value.items)
        && typeof value.cleanupPending === "boolean";
}
