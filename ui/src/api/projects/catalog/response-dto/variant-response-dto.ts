import { isRecord } from "../../../transport/envelopes";
import { isCount, isDate, isDateOrNull, isId, isRevision } from "../catalog-guards";

/** Вариант проекта: общий способ отображения объектов. archivedAt — null для активного. */
export interface VariantResponseDto {
    id: string;
    name: string;
    description: string;
    sortOrder: number;
    createdAt: string;
    updatedAt: string;
    archivedAt: string | null;
    revision: number;
}

export function isVariantResponseDto(value: unknown): value is VariantResponseDto {
    return isRecord(value) && isId(value.id)
        && typeof value.name === "string" && value.name.length > 0
        && typeof value.description === "string"
        && isCount(value.sortOrder)
        && isDate(value.createdAt) && isDate(value.updatedAt) && isDateOrNull(value.archivedAt)
        && isRevision(value.revision);
}
