import { isRecord } from "../../../transport/envelopes";
import { isCount, isDate, isDateOrNull, isHexColor, isId, isRevision } from "../catalog-guards";

/**
 * A class variant binds a project variant (variantId) to a class; the name and description come from the project variant.
 * color is the saved color or null; displayColor is the box color to show (the saved or the default one).
 * available: neither the binding nor the class nor the project variant is archived; only such a binding accepts images.
 * opaqueAssetCount counts images without an alpha channel (generation needs an alpha channel);
 * maxAssetWidth/Height are the largest image sides for the scale warning (0: no images).
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
