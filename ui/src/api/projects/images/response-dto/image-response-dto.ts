import { isResourceUrl, type ResourceUrl } from "../../../resources/resource-url";
import { isRecord } from "../../../transport/envelopes";
import { isCount, isDate, isDateOrNull, isId, isRevision } from "../../catalog/catalog-guards";
import { isImageGroupResponseDto, type ImageGroupResponseDto } from "./image-group-response-dto";

export type ImageFormat = "png" | "jpeg" | "webp" | "bmp";

/** reviewed — разметка проверена, в том числе осознанно пустой кадр. */
export type ImageReviewState = "unreviewed" | "reviewed";

export type ImageLineageKind = "file" | "video" | "generated";

const formats: readonly string[] = ["png", "jpeg", "webp", "bmp"] satisfies ImageFormat[];
const reviewStates: readonly string[] = ["unreviewed", "reviewed"] satisfies ImageReviewState[];
const lineageKinds: readonly string[] = ["file", "video", "generated"] satisfies ImageLineageKind[];

export const isImageReviewState = (value: unknown): value is ImageReviewState => typeof value === "string" && reviewStates.includes(value);

/**
 * Происхождение кадра («Источник: …»). sourceFamilyId объединяет кадры одного источника (file:<pixelHash>,
 * video:<хеш видео>). Поля видео и генерации — null у других видов.
 */
export interface ImageLineageDto {
    kind: ImageLineageKind;
    sourceFamilyId: string;
    videoName: string | null;
    timestampMs: number | null;
    frameIndex: number | null;
    generationRunId: string | null;
    sampleIndex: number | null;
    recipeRevision: number | null;
}

/**
 * Изображение группы. width/height — с учётом EXIF-ориентации, как его показывает <img>; координаты разметки относятся к
 * этому кадру. revision растёт при сохранении разметки, архиве и перемещении (её передают в setArchived и move);
 * annotationRevision — текущая ревизия разметки (её передают в saveAnnotation). objectCount и reviewState — из текущей
 * разметки («W × H · рамок: N / без рамок · проверено»). originalUrl — для просмотрщика, thumbnailUrl (JPEG до 256 px) —
 * для галереи; URL действуют ограниченное время и только на этой странице.
 */
export interface ImageResponseDto {
    id: string;
    groupId: string;
    displayName: string;
    format: ImageFormat;
    width: number;
    height: number;
    byteSize: number;
    revision: number;
    annotationRevision: number;
    objectCount: number;
    reviewState: ImageReviewState;
    createdAt: string;
    archivedAt: string | null;
    lineage: ImageLineageDto;
    originalUrl: ResourceUrl;
    thumbnailUrl: ResourceUrl;
}

const isCountOrNull = (value: unknown) => value === null || isCount(value);

function isImageLineageDto(value: unknown): value is ImageLineageDto {
    return isRecord(value) && typeof value.kind === "string" && lineageKinds.includes(value.kind) && typeof value.sourceFamilyId === "string"
        && (value.videoName === null || typeof value.videoName === "string") && isCountOrNull(value.timestampMs) && isCountOrNull(value.frameIndex)
        && (value.generationRunId === null || isId(value.generationRunId)) && isCountOrNull(value.sampleIndex) && isCountOrNull(value.recipeRevision);
}

export function isImageResponseDto(value: unknown): value is ImageResponseDto {
    return isRecord(value) && isId(value.id) && isId(value.groupId) && typeof value.displayName === "string"
        && typeof value.format === "string" && formats.includes(value.format)
        && isCount(value.width) && (value.width as number) > 0 && isCount(value.height) && (value.height as number) > 0
        && isCount(value.byteSize) && isRevision(value.revision) && isRevision(value.annotationRevision) && isCount(value.objectCount)
        && isImageReviewState(value.reviewState) && isDate(value.createdAt) && isDateOrNull(value.archivedAt) && isImageLineageDto(value.lineage)
        && isResourceUrl(value.originalUrl) && isResourceUrl(value.thumbnailUrl);
}

export function isImageList(value: unknown): value is ImageResponseDto[] {
    return Array.isArray(value) && value.every(isImageResponseDto);
}

/** Страница по порядку импорта; nextCursor — значение after для следующей страницы того же фильтра, null на последней. */
export interface ImagePageResponseDto {
    items: ImageResponseDto[];
    nextCursor: string | null;
}

export function isImagePageResponseDto(value: unknown): value is ImagePageResponseDto {
    return isRecord(value) && isImageList(value.items)
        && (value.nextCursor === null || (typeof value.nextCursor === "string" && value.nextCursor.length > 0));
}

/** Результат перемещения: обе группы с новыми ревизиями и перемещённые изображения (в конце порядка цели). */
export interface MovedImagesResponseDto {
    source: ImageGroupResponseDto;
    target: ImageGroupResponseDto;
    images: ImageResponseDto[];
}

export function isMovedImagesResponseDto(value: unknown): value is MovedImagesResponseDto {
    return isRecord(value) && isImageGroupResponseDto(value.source) && isImageGroupResponseDto(value.target) && isImageList(value.images);
}

/**
 * Выбор в нативном диалоге. selectionToken — null, если пользователь закрыл диалог; токен действует 30 минут и только на
 * этой странице. Для файлов fileCount — их число, для папки folderName — имя папки без пути (состав определит задача).
 */
export interface ImageSelectionResponseDto {
    kind: "files" | "folder";
    selectionToken: string | null;
    fileCount: number | null;
    folderName: string | null;
}

export function isImageSelectionResponseDto(value: unknown): value is ImageSelectionResponseDto {
    return isRecord(value) && (value.kind === "files" || value.kind === "folder")
        && (value.selectionToken === null || (typeof value.selectionToken === "string" && value.selectionToken.length > 0))
        && isCountOrNull(value.fileCount) && (value.folderName === null || typeof value.folderName === "string");
}
