import type { CatalogWorkspaceResponseDto } from "@/api/projects/catalog/response-dto/catalog-workspace-response-dto";
import type { ClassResponseDto } from "@/api/projects/catalog/response-dto/class-response-dto";
import type { ClassVariantResponseDto } from "@/api/projects/catalog/response-dto/class-variant-response-dto";
import { defaultColor } from "@/lib/variant-color";

export interface PaletteBinding {
    binding: ClassVariantResponseDto;
    name: string;
    color: string;
}

/** Цвета и подписи рамок по каталогу: «Класс · Вариант», цвет варианта класса или класса, если варианта нет. */
export interface AnnotationPalette {
    colorOf: (classId: string, classVariantId: string | null) => string;
    labelOf: (classId: string, classVariantId: string | null) => string;
    /** Активные классы — выбор «Класс объекта». */
    activeClasses: readonly ClassResponseDto[];
    /** Доступные варианты класса — выбор «Вариант объекта». */
    bindingsOf: (classId: string) => PaletteBinding[];
    binding: (classVariantId: string) => ClassVariantResponseDto | undefined;
}

export function createPalette(workspace: CatalogWorkspaceResponseDto | undefined): AnnotationPalette {
    const classes = new Map(workspace?.classes.map(item => [item.id, item]) ?? []);
    const variants = new Map(workspace?.variants.map(item => [item.id, item]) ?? []);
    const bindings = new Map(workspace?.classVariants.map(item => [item.id, item]) ?? []);
    const colorOf = (classId: string, classVariantId: string | null) =>
        (classVariantId && bindings.get(classVariantId)?.displayColor) || defaultColor(classId);
    return {
        colorOf,
        labelOf: (classId, classVariantId) => {
            const className = classes.get(classId)?.name ?? "Класс";
            const binding = classVariantId ? bindings.get(classVariantId) : undefined;
            const variant = binding ? variants.get(binding.variantId) : undefined;
            return variant ? `${className} · ${variant.name}` : className;
        },
        activeClasses: workspace?.classes.filter(item => !item.archivedAt) ?? [],
        bindingsOf: classId => (workspace?.classVariants ?? [])
            .filter(item => item.classId === classId && item.available)
            .map(item => ({ binding: item, name: variants.get(item.variantId)?.name ?? "Вариант", color: item.displayColor })),
        binding: classVariantId => bindings.get(classVariantId),
    };
}
