import { isResourceUrl, type ResourceUrl } from "../../../resources/resource-url";
import { isRecord } from "../../../transport/envelopes";
import { isCount, isDate, isId } from "../catalog-guards";

export type ClassAssetFormat = "png" | "jpeg" | "webp" | "bmp";

const formats: readonly string[] = ["png", "jpeg", "webp", "bmp"] satisfies ClassAssetFormat[];

/**
 * Исходное изображение варианта класса. width/height — с учётом EXIF-ориентации; hasAlpha — есть альфа-канал.
 * originalUrl и thumbnailUrl (JPEG до 256 px) — URL канала ресурсов: действуют ограниченное время и только на этой
 * странице; после ошибки загрузки страница изображений перечитывается.
 */
export interface ClassAssetResponseDto {
    id: string;
    classVariantId: string;
    format: ClassAssetFormat;
    width: number;
    height: number;
    hasAlpha: boolean;
    byteSize: number;
    createdAt: string;
    originalUrl: ResourceUrl;
    thumbnailUrl: ResourceUrl;
}

export function isClassAssetResponseDto(value: unknown): value is ClassAssetResponseDto {
    return isRecord(value) && isId(value.id) && isId(value.classVariantId)
        && typeof value.format === "string" && formats.includes(value.format)
        && isCount(value.width) && (value.width as number) > 0 && isCount(value.height) && (value.height as number) > 0
        && typeof value.hasAlpha === "boolean" && isCount(value.byteSize) && isDate(value.createdAt)
        && isResourceUrl(value.originalUrl) && isResourceUrl(value.thumbnailUrl);
}

/** Страница по порядку добавления; nextCursor — значение after для следующей страницы, null на последней. */
export interface ClassAssetPageResponseDto {
    items: ClassAssetResponseDto[];
    nextCursor: string | null;
}

export function isClassAssetPageResponseDto(value: unknown): value is ClassAssetPageResponseDto {
    return isRecord(value) && Array.isArray(value.items) && value.items.every(isClassAssetResponseDto)
        && (value.nextCursor === null || (typeof value.nextCursor === "string" && value.nextCursor.length > 0));
}

/**
 * Результат нативного диалога выбора файлов. selectionToken — null, если пользователь закрыл диалог;
 * токен действует 30 минут и только на этой странице. Пути файлов в UI не передаются.
 */
export interface ClassAssetFileSelectionResponseDto {
    selectionToken: string | null;
    fileCount: number;
}

export function isClassAssetFileSelectionResponseDto(value: unknown): value is ClassAssetFileSelectionResponseDto {
    return isRecord(value) && isCount(value.fileCount)
        && (value.selectionToken === null ? value.fileCount === 0 : typeof value.selectionToken === "string" && (value.fileCount as number) > 0);
}
