import "@/components/backgrounds/locale";
import { i18n } from "@/i18n";
import type { InputSizeDto } from "../input-size-dto";
import type { BackgroundBlurRequestDto, BackgroundRegionRequestDto } from "./request-dto/import-backgrounds-request-dto";

/** The same limits the host checks (ImportBackgroundsInput.Validate and the controller). */
export const maxBlurRadius = 1000;
/** The radius slider is 0–32 px in steps of 0.5; the inputs take up to maxBlurRadius. */
export const blurSliderMax = 32;
export const blurStep = 0.5;
/** A radius below this value leaves the background sharp. */
export const sharpBelowRadius = 0.3;
export const maxBlurSeed = 4_294_967_295;
/** "Cut from images" in one run. */
export const maxCropSources = 500;
/** previewImport returns up to this many samples. */
export const maxSamples = 6;
/** The frame preview for the crop frame is not larger than this side. */
export const previewSide = 1600;

/** Initial values: "Radius from, px" 0, "Radius to, px" 4. */
export const defaultBlur: BackgroundBlurRequestDto = { minRadius: 0, maxRadius: 4, seed: null };

/** "How many backgrounds to cut" 20, "Take a frame every, s" 1, "Start at, s" 0, "Skip repeated frames" on. */
export const defaultVideoImport = { count: 20, intervalMs: 1000, startMs: 0, skipSimilar: true } as const;

/** The blur error code or null: finite radii, 0 ≤ from ≤ to ≤ 1000; seed is an integer 0–4 294 967 295 or null. */
export function blurError(blur: BackgroundBlurRequestDto): "backgrounds.blur_invalid" | null {
    const { minRadius, maxRadius, seed } = blur;
    const radiiValid = Number.isFinite(minRadius) && Number.isFinite(maxRadius) && minRadius >= 0 && maxRadius <= maxBlurRadius && minRadius <= maxRadius;
    const seedValid = seed === undefined || seed === null || (Number.isSafeInteger(seed) && seed >= 0 && seed <= maxBlurSeed);
    return radiiValid && seedValid ? null : "backgrounds.blur_invalid";
}

/**
 * "Up to K frames are available with this step.": marks startMs + i × intervalMs strictly before the end of the video,
 * max(0, ⌊(durationMs − 1 − startMs) / intervalMs⌋ + 1), the same formula as BackgroundVideoSource.AvailableFrames.
 */
export function availableVideoFrames(durationMs: number, startMs: number, intervalMs: number): number {
    if (!Number.isSafeInteger(startMs) || !Number.isSafeInteger(intervalMs) || intervalMs < 1 || startMs < 0 || startMs >= durationMs) return 0;
    return Math.floor((durationMs - 1 - startMs) / intervalMs) + 1;
}

/** How a file or a frame fits the set: exact, larger (a frame can be cut) or smaller. */
export function sourceFit(width: number, height: number, target: InputSizeDto): "exact" | "larger" | "smaller" {
    if (width === target.width && height === target.height) return "exact";
    return width >= target.width && height >= target.height ? "larger" : "smaller";
}

/** A frame of the set size with the corner region fits into an image width × height. */
export function regionFits(region: BackgroundRegionRequestDto, width: number, height: number, target: InputSizeDto): boolean {
    return Number.isSafeInteger(region.left) && Number.isSafeInteger(region.top) && region.left >= 0 && region.top >= 0
        && region.left + target.width <= width && region.top + target.height <= height;
}

/** The frame at the center of the image (the default position without an earlier decision). */
export function centeredRegion(width: number, height: number, target: InputSizeDto): BackgroundRegionRequestDto {
    return { left: Math.max(0, Math.floor((width - target.width) / 2)), top: Math.max(0, Math.floor((height - target.height) / 2)) };
}

export type VideoImportError = "backgrounds.video_too_small" | "backgrounds.region_invalid" | "video.frames_invalid" | "video.range_invalid"
    | "backgrounds.count_invalid";

/**
 * The "Cut from video" error code before sending, or null: the same checks as the host's: the video is not smaller than the set, the
 * frame is inside the image, start ≥ 0 and step ≥ 1 ms, the start is before the end, 1 ≤ count ≤ availableVideoFrames.
 */
export function videoImportError(video: { width: number; height: number; durationMs: number }, target: InputSizeDto,
    request: { region: BackgroundRegionRequestDto; startMs: number; intervalMs: number; count: number }): VideoImportError | null {
    if (video.width < target.width || video.height < target.height) return "backgrounds.video_too_small";
    if (!regionFits(request.region, video.width, video.height, target)) return "backgrounds.region_invalid";
    if (!Number.isSafeInteger(request.startMs) || !Number.isSafeInteger(request.intervalMs) || request.startMs < 0 || request.intervalMs < 1) {
        return "video.frames_invalid";
    }
    const available = availableVideoFrames(video.durationMs, request.startMs, request.intervalMs);
    if (available === 0) return "video.range_invalid";
    return Number.isSafeInteger(request.count) && request.count >= 1 && request.count <= available ? null : "backgrounds.count_invalid";
}

/**
 * The word after a count of backgrounds in the plural form of the current language ("background / backgrounds"). The backgrounds
 * section uses whole plural sentences of its catalog; this word serves lines elsewhere that put a count in front of it.
 */
export function backgroundsWord(count: number): string {
    return i18n.t("backgrounds:word", { count });
}
