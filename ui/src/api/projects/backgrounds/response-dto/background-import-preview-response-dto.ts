import { isResourceUrl, type ResourceUrl } from "../../../resources/resource-url";
import { isRecord } from "../../../transport/envelopes";
import { isCount } from "../../catalog/catalog-guards";
import { isInputSizeDto, type InputSizeDto } from "../../input-size-dto";
import { isBackgroundCropAreaDto, type BackgroundCropAreaDto } from "./background-response-dto";

/** Размытие запуска с выбранным seed: «размытие a–b px, seed S». */
export interface BackgroundBlurResponseDto {
    minRadius: number;
    maxRadius: number;
    seed: number;
}

/**
 * Пример будущего фона. sourceIndex — index файла выбора (у видео — номер кадра от начала), timestampMs — отметка кадра видео.
 * Пропуск: reason — код (backgrounds.wrong_size, backgrounds.crop_out_of_bounds, backgrounds.image_unreadable,
 * backgrounds.similar, backgrounds.duplicate, video.frame_unavailable; the text is backgroundReasonText,
 * imageUrl — null. Иначе imageUrl — PNG будущего фона ровно размера набора (временный URL).
 */
export interface BackgroundSampleResponseDto {
    sourceIndex: number;
    sourceName: string;
    timestampMs: number | null;
    reason: string | null;
    actualWidth: number | null;
    actualHeight: number | null;
    crop: BackgroundCropAreaDto | null;
    blurRadius: number | null;
    imageUrl: ResourceUrl | null;
}

/** «Примеры будущих фонов W × H · размытие a–b px, seed S» (blur null — «без размытия») и до 6 примеров. */
export interface BackgroundImportPreviewResponseDto {
    targetSize: InputSizeDto;
    blur: BackgroundBlurResponseDto | null;
    samples: BackgroundSampleResponseDto[];
}

const isCountOrNull = (value: unknown) => value === null || isCount(value);
const isRadius = (value: unknown): value is number => typeof value === "number" && Number.isFinite(value) && value >= 0;

export function isBackgroundBlurResponseDto(value: unknown): value is BackgroundBlurResponseDto {
    return isRecord(value) && isRadius(value.minRadius) && isRadius(value.maxRadius) && isCount(value.seed);
}

function isSample(value: unknown): value is BackgroundSampleResponseDto {
    return isRecord(value) && isCount(value.sourceIndex) && typeof value.sourceName === "string" && isCountOrNull(value.timestampMs)
        && (value.reason === null || typeof value.reason === "string")
        && isCountOrNull(value.actualWidth) && isCountOrNull(value.actualHeight) && (value.crop === null || isBackgroundCropAreaDto(value.crop))
        && (value.blurRadius === null || isRadius(value.blurRadius)) && (value.imageUrl === null || isResourceUrl(value.imageUrl));
}

export function isBackgroundImportPreviewResponseDto(value: unknown): value is BackgroundImportPreviewResponseDto {
    return isRecord(value) && isInputSizeDto(value.targetSize) && (value.blur === null || isBackgroundBlurResponseDto(value.blur))
        && Array.isArray(value.samples) && value.samples.every(isSample);
}
