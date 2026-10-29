import { isResourceUrl, type ResourceUrl } from "../../../resources/resource-url";
import { isRecord } from "../../../transport/envelopes";
import { isCount } from "../../catalog/catalog-guards";
import { isInputSizeDto, type InputSizeDto } from "../../input-size-dto";
import { isBackgroundCropAreaDto, type BackgroundCropAreaDto } from "./background-response-dto";

/** The blur of a run with the chosen seed: "blur a–b px, seed S". */
export interface BackgroundBlurResponseDto {
    minRadius: number;
    maxRadius: number;
    seed: number;
}

/**
 * A sample of a future background. sourceIndex is the index of a file of the selection (for a video the frame number from the start), timestampMs the mark of a video frame.
 * A skip: reason is a code (backgrounds.wrong_size, backgrounds.crop_out_of_bounds, backgrounds.image_unreadable,
 * backgrounds.similar, backgrounds.duplicate, video.frame_unavailable; the text is backgroundReasonText,
 * imageUrl is null. Otherwise imageUrl is a PNG of the future background of exactly the set size (a temporary URL).
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

/** "Samples of future backgrounds W × H · blur a–b px, seed S" (blur null: "no blur") and up to 6 samples. */
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
