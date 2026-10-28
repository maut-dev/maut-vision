import type { InputSizeDto } from "../input-size-dto";
import type { BackgroundBlurRequestDto, BackgroundRegionRequestDto } from "./request-dto/import-backgrounds-request-dto";

/** Те же пределы, что проверяет host (ImportBackgroundsInput.Validate и контроллер). */
export const maxBlurRadius = 1000;
/** Ползунок радиуса — 0–32 px с шагом 0,5; поля ввода принимают до maxBlurRadius. */
export const blurSliderMax = 32;
export const blurStep = 0.5;
/** Радиус меньше этого значения оставляет фон резким. */
export const sharpBelowRadius = 0.3;
export const maxBlurSeed = 4_294_967_295;
/** «Нарезать из изображений» за один запуск. */
export const maxCropSources = 500;
/** До стольких примеров возвращает previewImport. */
export const maxSamples = 6;
/** Превью кадра для рамки — не больше этой стороны. */
export const previewSide = 1600;

/** Начальные значения: «Радиус от, px» 0, «Радиус до, px» 4. */
export const defaultBlur: BackgroundBlurRequestDto = { minRadius: 0, maxRadius: 4, seed: null };

/** «Сколько фонов нарезать» 20, «Брать кадр раз в, с» 1, «Начать с, с» 0, «Пропускать повторяющиеся кадры» включено. */
export const defaultVideoImport = { count: 20, intervalMs: 1000, startMs: 0, skipSimilar: true } as const;

/** Код ошибки размытия или null: радиусы конечные, 0 ≤ от ≤ до ≤ 1000; seed — целое 0–4 294 967 295 или null. */
export function blurError(blur: BackgroundBlurRequestDto): "backgrounds.blur_invalid" | null {
    const { minRadius, maxRadius, seed } = blur;
    const radiiValid = Number.isFinite(minRadius) && Number.isFinite(maxRadius) && minRadius >= 0 && maxRadius <= maxBlurRadius && minRadius <= maxRadius;
    const seedValid = seed === undefined || seed === null || (Number.isSafeInteger(seed) && seed >= 0 && seed <= maxBlurSeed);
    return radiiValid && seedValid ? null : "backgrounds.blur_invalid";
}

/**
 * «С таким шагом доступно до K кадров.»: отметки startMs + i × intervalMs строго раньше конца видео,
 * max(0, ⌊(durationMs − 1 − startMs) / intervalMs⌋ + 1) — та же формула, что BackgroundVideoSource.AvailableFrames.
 */
export function availableVideoFrames(durationMs: number, startMs: number, intervalMs: number): number {
    if (!Number.isSafeInteger(startMs) || !Number.isSafeInteger(intervalMs) || intervalMs < 1 || startMs < 0 || startMs >= durationMs) return 0;
    return Math.floor((durationMs - 1 - startMs) / intervalMs) + 1;
}

/** Как файл или кадр подходит набору: exact, larger (можно вырезать рамкой) или smaller. */
export function sourceFit(width: number, height: number, target: InputSizeDto): "exact" | "larger" | "smaller" {
    if (width === target.width && height === target.height) return "exact";
    return width >= target.width && height >= target.height ? "larger" : "smaller";
}

/** Рамка размера набора с углом region помещается в кадр width × height. */
export function regionFits(region: BackgroundRegionRequestDto, width: number, height: number, target: InputSizeDto): boolean {
    return Number.isSafeInteger(region.left) && Number.isSafeInteger(region.top) && region.left >= 0 && region.top >= 0
        && region.left + target.width <= width && region.top + target.height <= height;
}

/** Рамка в центре кадра (позиция по умолчанию, если нет прежнего решения). */
export function centeredRegion(width: number, height: number, target: InputSizeDto): BackgroundRegionRequestDto {
    return { left: Math.max(0, Math.floor((width - target.width) / 2)), top: Math.max(0, Math.floor((height - target.height) / 2)) };
}

export type VideoImportError = "backgrounds.video_too_small" | "backgrounds.region_invalid" | "video.frames_invalid" | "video.range_invalid"
    | "backgrounds.count_invalid";

/**
 * Код ошибки «Нарезать из видео» до отправки или null — те же проверки, что у host: видео не меньше набора, рамка внутри
 * кадра, начало ≥ 0 и шаг ≥ 1 мс, начало раньше конца, 1 ≤ count ≤ availableVideoFrames.
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

/** «N фон / фона / фонов». */
export function backgroundsWord(count: number): string {
    const tail = count % 100;
    if (tail >= 11 && tail <= 14) return "фонов";
    if (count % 10 === 1) return "фон";
    if (count % 10 >= 2 && count % 10 <= 4) return "фона";
    return "фонов";
}
