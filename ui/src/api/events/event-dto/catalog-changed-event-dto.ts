import { isCount, isId, isRevision } from "../../projects/catalog/catalog-guards";
import { isRecord } from "../../transport/envelopes";

/** Изменённый объект с новой ревизией или удалённый (revision = null). */
export interface CatalogEntityChangeDto {
    id: string;
    revision: number | null;
}

/**
 * Что изменилось в каталоге проекта. Если catalogRevision больше известного, UI перечитывает снимок
 * classes.getWorkspace; для привязок из assetsChangedIn — и их страницы изображений.
 */
export interface CatalogChangedDto {
    projectId: string;
    variants: CatalogEntityChangeDto[];
    classes: CatalogEntityChangeDto[];
    classVariants: CatalogEntityChangeDto[];
    assetsChangedIn: string[];
    classOrderRevision: number;
    catalogRevision: number;
}

export interface CatalogChangedEventDto {
    kind: "event";
    event: "catalog.changed";
    data: CatalogChangedDto;
}

function isCatalogEntityChangeDto(value: unknown): value is CatalogEntityChangeDto {
    return isRecord(value) && isId(value.id) && (value.revision === null || isRevision(value.revision));
}

const isChangeList = (value: unknown) => Array.isArray(value) && value.every(isCatalogEntityChangeDto);

export function isCatalogChangedDto(value: unknown): value is CatalogChangedDto {
    return isRecord(value) && typeof value.projectId === "string" && value.projectId.length > 0
        && isChangeList(value.variants) && isChangeList(value.classes) && isChangeList(value.classVariants)
        && Array.isArray(value.assetsChangedIn) && value.assetsChangedIn.every(isId)
        && isRevision(value.classOrderRevision) && isCount(value.catalogRevision);
}
