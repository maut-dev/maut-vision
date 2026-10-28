import { availableVideoFrames, backgroundsWord, defaultVideoImport, videoImportError } from "@/api/projects/backgrounds/background-import-rules";
import type { BackgroundRegionRequestDto } from "@/api/projects/backgrounds/request-dto/import-backgrounds-request-dto";
import type { InputSizeDto } from "@/api/projects/input-size-dto";

/** Поля «Нарезать из видео» в секундах, как их ввёл пользователь; в запрос уходят миллисекунды. */
export interface VideoFields {
    count: string;
    interval: string;
    start: string;
    skipSimilar: boolean;
}

export const defaultVideoFields: VideoFields = {
    count: String(defaultVideoImport.count),
    interval: String(defaultVideoImport.intervalMs / 1000),
    start: String(defaultVideoImport.startMs / 1000),
    skipSimilar: defaultVideoImport.skipSimilar,
};

export interface VideoRange {
    startMs: number;
    intervalMs: number;
    count: number;
}

export function videoRangeOf(fields: VideoFields): VideoRange {
    return {
        startMs: Math.round(Number(fields.start) * 1000),
        intervalMs: Math.round(Number(fields.interval) * 1000),
        count: Number(fields.count),
    };
}

interface VideoInfo {
    width: number;
    height: number;
    durationMs: number;
}

/** «С таким шагом доступно до K кадров.» — 0, если поля неверны. */
export function videoFramesAvailable(video: VideoInfo, range: VideoRange): number {
    return availableVideoFrames(video.durationMs, range.startMs, range.intervalMs);
}

/**
 * Сообщение проверки до отправки или null. Правило — videoImportError, то же, что у host;
 * текст уточняет, какое поле неверно.
 */
export function videoFormMessage(video: VideoInfo, target: InputSizeDto, region: BackgroundRegionRequestDto, fields: VideoFields): string | null {
    const range = videoRangeOf(fields);
    const code = videoImportError(video, target, { region, ...range });
    const seconds = (video.durationMs / 1000).toFixed(1);
    switch (code) {
        case null:
            return null;
        case "backgrounds.video_too_small":
            return `Видео ${video.width}×${video.height} меньше входа модели ${target.width}×${target.height}: рамка не помещается.`;
        case "backgrounds.region_invalid":
            return "Рамка выходит за границы кадра.";
        case "video.frames_invalid":
            return !Number.isSafeInteger(range.intervalMs) || range.intervalMs < 1 ? "Шаг — положительное число секунд." : `Начало — от 0 до ${seconds} с.`;
        case "video.range_invalid":
            return `Начало — от 0 до ${seconds} с.`;
        case "backgrounds.count_invalid": {
            if (!Number.isSafeInteger(range.count) || range.count < 1) return "Количество фонов — положительное целое число.";
            const available = videoFramesAvailable(video, range);
            return `С шагом ${fields.interval} с из видео получится не больше ${available} ${backgroundsWord(available)}. Уменьшите шаг или количество.`;
        }
    }
}
