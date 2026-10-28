import { isRecord } from "../../../transport/envelopes";
import { isCount, isDate, isDateOrNull, isHexColor, isId, isRevision } from "../catalog-guards";

/**
 * Вариант класса — привязка варианта проекта (variantId) к классу; название и описание берутся у варианта проекта.
 * color — сохранённый цвет или null; displayColor — цвет рамок для показа (сохранённый или по умолчанию).
 * available — ни привязка, ни класс, ни вариант проекта не в архиве: только в такую привязку можно добавлять изображения.
 * opaqueAssetCount — изображения без альфа-канала (генерации нужен альфа-канал);
 * maxAssetWidth/Height — наибольшие стороны изображений для предупреждения о масштабе (0 — изображений нет).
 */
export interface ClassVariantResponseDto {
    id: string;
    classId: string;
    variantId: string;
    color: string | null;
    displayColor: string;
    createdAt: string;
    updatedAt: string;
    archivedAt: string | null;
    revision: number;
    available: boolean;
    assetCount: number;
    opaqueAssetCount: number;
    maxAssetWidth: number;
    maxAssetHeight: number;
}

export function isClassVariantResponseDto(value: unknown): value is ClassVariantResponseDto {
    return isRecord(value) && isId(value.id) && isId(value.classId) && isId(value.variantId)
        && (value.color === null || isHexColor(value.color)) && isHexColor(value.displayColor)
        && isDate(value.createdAt) && isDate(value.updatedAt) && isDateOrNull(value.archivedAt)
        && isRevision(value.revision) && typeof value.available === "boolean"
        && isCount(value.assetCount) && isCount(value.opaqueAssetCount)
        && isCount(value.maxAssetWidth) && isCount(value.maxAssetHeight);
}
