import { isId, isRevision } from "../../projects/catalog/catalog-guards";
import { isRecord } from "../../transport/envelopes";

/** Изменённый набор или фон с новой ревизией; revision = null — удалён. */
export interface BackgroundsEntityChangeDto {
    id: string;
    revision: number | null;
}

/**
 * Что изменилось в фонах проекта. sets — наборы с новыми ревизиями или удалённые; items — фоны, изменённые явной операцией
 * («Убрать», «Восстановить», удаление выбранных); itemsChangedIn — наборы, у которых изменился состав или счётчики (подготовка
 * приходит не чаще раза в 500 мс на набор): UI перечитывает их набор и загруженные страницы.
 */
export interface BackgroundsChangedDto {
    projectId: string;
    sets: BackgroundsEntityChangeDto[];
    items: BackgroundsEntityChangeDto[];
    itemsChangedIn: string[];
}

export interface BackgroundsChangedEventDto {
    kind: "event";
    event: "backgrounds.changed";
    data: BackgroundsChangedDto;
}

function isChange(value: unknown): value is BackgroundsEntityChangeDto {
    return isRecord(value) && isId(value.id) && (value.revision === null || isRevision(value.revision));
}

export function isBackgroundsChangedDto(value: unknown): value is BackgroundsChangedDto {
    return isRecord(value) && typeof value.projectId === "string" && value.projectId.length > 0
        && Array.isArray(value.sets) && value.sets.every(isChange) && Array.isArray(value.items) && value.items.every(isChange)
        && Array.isArray(value.itemsChangedIn) && value.itemsChangedIn.every(isId);
}
