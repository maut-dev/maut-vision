import { isRecord } from "../../../transport/envelopes";
import { isRevision } from "../catalog-guards";
import { isClassResponseDto, type ClassResponseDto } from "./class-response-dto";
import { isClassVariantResponseDto, type ClassVariantResponseDto } from "./class-variant-response-dto";
import { isVariantResponseDto, type VariantResponseDto } from "./variant-response-dto";

/**
 * Снимок каталога проекта без изображений: варианты и классы в порядке sortOrder (включая архивные), привязки.
 * classOrderRevision передаётся в classes.reorder. catalogRevision растёт с каждым изменением каталога:
 * снимок или событие catalog.changed с меньшим значением устарели.
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
