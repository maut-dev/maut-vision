import { isResourceUrl, type ResourceUrl } from "../../../resources/resource-url";
import { isRecord } from "../../../transport/envelopes";
import { isCount, isDate, isDateOrNull, isId, isRevision } from "../../catalog/catalog-guards";

/** uploaded — «Готовое изображение», image-crop — «Фрагмент изображения», video-frame — «Кадр видео». */
export type BackgroundOriginKind = "uploaded" | "image-crop" | "video-frame";

const originKinds: readonly string[] = ["uploaded", "image-crop", "video-frame"] satisfies BackgroundOriginKind[];

/** Вырезанная область исходного кадра в его пикселях: «Область: x, y · w × h px». */
export interface BackgroundCropAreaDto {
    left: number;
    top: number;
    width: number;
    height: number;
}

/**
 * Происхождение фона. sourceName — имя файла или видео (подпись карточки); videoName, timestampMs, frameIndex — только у кадра
 * видео. crop — «вырезан из x L, y T» (null — кадр целиком). blurRadius — «размытие r px» (0 — резкий), null — без размытия.
 */
export interface BackgroundOriginDto {
    kind: BackgroundOriginKind;
    operationId: string;
    sourceName: string;
    videoName: string | null;
    timestampMs: number | null;
    frameIndex: number | null;
    crop: BackgroundCropAreaDto | null;
    blurRadius: number | null;
    processorVersion: string;
}

/**
 * Фон — PNG ровно размера набора. revision передаётся в setRemoved; removedAt — убран («Восстановить»). originalUrl — для
 * просмотрщика, thumbnailUrl (JPEG до 256 px) — для галереи; URL действуют ограниченное время и только на этой странице.
 */
export interface BackgroundResponseDto {
    id: string;
    setId: string;
    width: number;
    height: number;
    byteSize: number;
    revision: number;
    createdAt: string;
    removedAt: string | null;
    origin: BackgroundOriginDto;
    originalUrl: ResourceUrl;
    thumbnailUrl: ResourceUrl;
}

const isCountOrNull = (value: unknown) => value === null || isCount(value);
const isRadius = (value: unknown) => typeof value === "number" && Number.isFinite(value) && value >= 0;

export function isBackgroundCropAreaDto(value: unknown): value is BackgroundCropAreaDto {
    return isRecord(value) && isCount(value.left) && isCount(value.top) && isCount(value.width) && isCount(value.height);
}

function isBackgroundOriginDto(value: unknown): value is BackgroundOriginDto {
    return isRecord(value) && typeof value.kind === "string" && originKinds.includes(value.kind) && typeof value.operationId === "string"
        && typeof value.sourceName === "string" && (value.videoName === null || typeof value.videoName === "string")
        && isCountOrNull(value.timestampMs) && isCountOrNull(value.frameIndex)
        && (value.crop === null || isBackgroundCropAreaDto(value.crop)) && (value.blurRadius === null || isRadius(value.blurRadius))
        && typeof value.processorVersion === "string";
}

export function isBackgroundResponseDto(value: unknown): value is BackgroundResponseDto {
    return isRecord(value) && isId(value.id) && isId(value.setId) && isCount(value.width) && isCount(value.height) && isCount(value.byteSize)
        && isRevision(value.revision) && isDate(value.createdAt) && isDateOrNull(value.removedAt) && isBackgroundOriginDto(value.origin)
        && isResourceUrl(value.originalUrl) && isResourceUrl(value.thumbnailUrl);
}

export function isBackgroundList(value: unknown): value is BackgroundResponseDto[] {
    return Array.isArray(value) && value.every(isBackgroundResponseDto);
}

/** Страница по порядку добавления; nextCursor — значение after для следующей страницы того же фильтра, null на последней. */
export interface BackgroundPageResponseDto {
    items: BackgroundResponseDto[];
    nextCursor: string | null;
}

export function isBackgroundPageResponseDto(value: unknown): value is BackgroundPageResponseDto {
    return isRecord(value) && isBackgroundList(value.items)
        && (value.nextCursor === null || (typeof value.nextCursor === "string" && value.nextCursor.length > 0));
}
