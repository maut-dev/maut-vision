import { isId, isRevision } from "../../projects/catalog/catalog-guards";
import { isRecord } from "../../transport/envelopes";

/** Изменённая версия с новой ревизией; revision = null — удалена. */
export interface DatasetEntityChangeDto {
    id: string;
    revision: number | null;
}

/**
 * Что изменилось в датасетах проекта: versions — версии с новыми ревизиями (появилась после сборки, изменены название или архив,
 * проверена целостность) или удалённые: перечитать list / get. Прогресс и итог операций — job.changed.
 */
export interface DatasetsChangedDto {
    projectId: string;
    versions: DatasetEntityChangeDto[];
}

export interface DatasetsChangedEventDto {
    kind: "event";
    event: "datasets.changed";
    data: DatasetsChangedDto;
}

export function isDatasetsChangedDto(value: unknown): value is DatasetsChangedDto {
    return isRecord(value) && typeof value.projectId === "string" && value.projectId.length > 0 && Array.isArray(value.versions)
        && value.versions.every(item => isRecord(item) && isId(item.id) && (item.revision === null || isRevision(item.revision)));
}
