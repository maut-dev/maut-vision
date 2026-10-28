import { isRecord } from "../../../transport/envelopes";
import { isCount, isDate, isDateOrNull, isId, isRevision } from "../catalog-guards";

/**
 * Класс — объект распознавания. sortOrder задаёт порядок классов: при сборке датасета выбранные классы получают
 * индексы от 0 без пропусков в этом порядке. Индекс в списке — место среди активных классов.
 */
export interface ClassResponseDto {
    id: string;
    name: string;
    description: string;
    sortOrder: number;
    createdAt: string;
    updatedAt: string;
    archivedAt: string | null;
    revision: number;
}

export function isClassResponseDto(value: unknown): value is ClassResponseDto {
    return isRecord(value) && isId(value.id)
        && typeof value.name === "string" && value.name.length > 0
        && typeof value.description === "string"
        && isCount(value.sortOrder)
        && isDate(value.createdAt) && isDate(value.updatedAt) && isDateOrNull(value.archivedAt)
        && isRevision(value.revision);
}

export interface ClassOrderResponseDto {
    classes: ClassResponseDto[];
    orderRevision: number;
}

export function isClassOrderResponseDto(value: unknown): value is ClassOrderResponseDto {
    return isRecord(value) && Array.isArray(value.classes) && value.classes.every(isClassResponseDto) && isRevision(value.orderRevision);
}
