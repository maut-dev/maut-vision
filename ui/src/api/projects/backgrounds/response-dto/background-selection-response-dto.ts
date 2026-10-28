import { isResourceUrl, type ResourceUrl } from "../../../resources/resource-url";
import { isRecord } from "../../../transport/envelopes";
import { isCount } from "../../catalog/catalog-guards";

/** exact — ровно размер набора, larger — можно вырезать рамкой, smaller — будет пропущен, unreadable — не читается. */
export type BackgroundSourceFit = "exact" | "larger" | "smaller" | "unreadable";

const fits: readonly string[] = ["exact", "larger", "smaller", "unreadable"] satisfies BackgroundSourceFit[];

/** Файл выбора: index — номер в выборе (его передают в previewSource, sourceIndices, crops); размер с учётом ориентации. */
export interface BackgroundSourceFileResponseDto {
    index: number;
    name: string;
    width: number | null;
    height: number | null;
    fit: BackgroundSourceFit;
}

/**
 * Выбор файлов (backgrounds.selectFiles). selectionToken — null, если диалог закрыт (files пуст); действует 30 минут и только
 * на этой странице. Файлы — в естественном порядке имён; exact/larger/smaller/unreadable — счётчики для размера набора
 * targetWidth × targetHeight: «Готовые кадры» возьмут exact, остальные будут пропущены.
 */
export interface BackgroundFileSelectionResponseDto {
    selectionToken: string | null;
    targetWidth: number;
    targetHeight: number;
    files: BackgroundSourceFileResponseDto[];
    exact: number;
    larger: number;
    smaller: number;
    unreadable: number;
}

function isSourceFile(value: unknown): value is BackgroundSourceFileResponseDto {
    return isRecord(value) && isCount(value.index) && typeof value.name === "string"
        && (value.width === null || isCount(value.width)) && (value.height === null || isCount(value.height))
        && typeof value.fit === "string" && fits.includes(value.fit);
}

export function isBackgroundFileSelectionResponseDto(value: unknown): value is BackgroundFileSelectionResponseDto {
    return isRecord(value) && (value.selectionToken === null || (typeof value.selectionToken === "string" && value.selectionToken.length > 0))
        && isCount(value.targetWidth) && isCount(value.targetHeight) && Array.isArray(value.files) && value.files.every(isSourceFile)
        && isCount(value.exact) && isCount(value.larger) && isCount(value.smaller) && isCount(value.unreadable);
}

/**
 * Кадр для рамки: sourceIndex — файл выбора (у видео null), timestampMs — отметка кадра видео (у файла null). width/height —
 * размер исходного кадра: координаты рамки считаются в нём. previewUrl — уменьшенная копия previewWidth × previewHeight
 * (не больше 1600 px), временная: действует ограниченное время и только на этой странице.
 */
export interface BackgroundSourcePreviewResponseDto {
    sourceIndex: number | null;
    timestampMs: number | null;
    name: string;
    width: number;
    height: number;
    previewUrl: ResourceUrl;
    previewWidth: number;
    previewHeight: number;
}

export function isBackgroundSourcePreviewResponseDto(value: unknown): value is BackgroundSourcePreviewResponseDto {
    return isRecord(value) && (value.sourceIndex === null || isCount(value.sourceIndex)) && (value.timestampMs === null || isCount(value.timestampMs))
        && typeof value.name === "string" && isCount(value.width) && isCount(value.height) && isResourceUrl(value.previewUrl)
        && isCount(value.previewWidth) && isCount(value.previewHeight);
}
