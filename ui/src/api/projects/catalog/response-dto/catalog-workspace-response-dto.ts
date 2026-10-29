import { isRecord } from "../../../transport/envelopes";
import { isRevision } from "../catalog-guards";
import { isClassResponseDto, type ClassResponseDto } from "./class-response-dto";
import { isClassVariantResponseDto, type ClassVariantResponseDto } from "./class-variant-response-dto";
import { isVariantResponseDto, type VariantResponseDto } from "./variant-response-dto";

/**
 * A snapshot of the project catalog without images: variants and classes in sortOrder (archived included), bindings.
 * classOrderRevision goes to classes.reorder. catalogRevision grows with every change of the catalog:
 * a snapshot or a catalog.changed event with a smaller value is stale.
 */
export interface CatalogWorkspaceResponseDto {
    variants: VariantResponseDto[];
    classes: ClassResponseDto[];
    classVariants: ClassVariantResponseDto[];
    classOrderRevision: number;
    catalogRevision: number;
}

export function isCatalogWorkspaceResponseDto(value: unknown): value is CatalogWorkspaceResponseDto {
    return isRecord(value)
        && Array.isArray(value.variants) && value.variants.every(isVariantResponseDto)
        && Array.isArray(value.classes) && value.classes.every(isClassResponseDto)
        && Array.isArray(value.classVariants) && value.classVariants.every(isClassVariantResponseDto)
        && isRevision(value.classOrderRevision) && isRevision(value.catalogRevision);
}
