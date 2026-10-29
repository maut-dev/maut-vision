import { isResourceUrl, type ResourceUrl } from "../../../resources/resource-url";
import { isRecord } from "../../../transport/envelopes";
import { isCount, isDate, isId } from "../catalog-guards";

export type ClassAssetFormat = "png" | "jpeg" | "webp" | "bmp";

const formats: readonly string[] = ["png", "jpeg", "webp", "bmp"] satisfies ClassAssetFormat[];

/**
 * A source image of a class variant. width/height follow the EXIF orientation; hasAlpha: it has an alpha channel.
 * originalUrl and thumbnailUrl (JPEG up to 256 px) are URLs of the resource channel: valid for a limited time and only on this
 * page; after a loading error the page of images is reread.
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

/** A page in the order of adding; nextCursor is the after value for the next page, null on the last one. */
export interface ClassAssetPageResponseDto {
    items: ClassAssetResponseDto[];
    nextCursor: string | null;
}

export function isClassAssetPageResponseDto(value: unknown): value is ClassAssetPageResponseDto {
    return isRecord(value) && Array.isArray(value.items) && value.items.every(isClassAssetResponseDto)
        && (value.nextCursor === null || (typeof value.nextCursor === "string" && value.nextCursor.length > 0));
}

/**
 * The result of the native file dialog. selectionToken is null when the user closed the dialog;
 * the token is valid for 30 minutes and on this page only. File paths are not passed to the UI.
 */
export interface ClassAssetFileSelectionResponseDto {
    selectionToken: string | null;
    fileCount: number;
}

export function isClassAssetFileSelectionResponseDto(value: unknown): value is ClassAssetFileSelectionResponseDto {
    return isRecord(value) && isCount(value.fileCount)
        && (value.selectionToken === null ? value.fileCount === 0 : typeof value.selectionToken === "string" && (value.fileCount as number) > 0);
}
