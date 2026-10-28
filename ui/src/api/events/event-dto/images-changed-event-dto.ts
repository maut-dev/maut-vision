import { isId, isRevision } from "../../projects/catalog/catalog-guards";
import { isRecord } from "../../transport/envelopes";

/** Изменённая группа или изображение с новой ревизией; revision = null — удалено. */
export interface ImagesEntityChangeDto {
    id: string;
    revision: number | null;
}

/**
 * Что изменилось в изображениях проекта. groups — группы с новыми ревизиями или удалённые; images — изображения, изменённые
 * явной операцией (разметка, архив, перемещение, удаление выбранных); imagesChangedIn — группы, у которых изменился состав
 * или счётчики (импорт приходит не чаще раза в 500 мс на группу): UI перечитывает их группу и загруженные страницы.
 */
export interface ImagesChangedDto {
    projectId: string;
    groups: ImagesEntityChangeDto[];
    images: ImagesEntityChangeDto[];
    imagesChangedIn: string[];
}

export interface ImagesChangedEventDto {
    kind: "event";
    event: "images.changed";
    data: ImagesChangedDto;
}

function isChange(value: unknown): value is ImagesEntityChangeDto {
    return isRecord(value) && isId(value.id) && (value.revision === null || isRevision(value.revision));
}

export function isImagesChangedDto(value: unknown): value is ImagesChangedDto {
    return isRecord(value) && typeof value.projectId === "string" && value.projectId.length > 0
        && Array.isArray(value.groups) && value.groups.every(isChange) && Array.isArray(value.images) && value.images.every(isChange)
        && Array.isArray(value.imagesChangedIn) && value.imagesChangedIn.every(isId);
}
