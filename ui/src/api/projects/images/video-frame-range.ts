/**
 * Кадры видео на отметках startMs + i × intervalMs, i = 0…count − 1 — те же правила, что проверяет host
 * (VideoFrameRange.Validate): начало ≥ 0, интервал ≥ 1 мс, количество ≥ 1 — иначе video.frames_invalid; последняя отметка
 * строго раньше конца видео — иначе video.range_invalid.
 */
export interface VideoFrameRange {
    startMs: number;
    intervalMs: number;
    count: number;
}

export type VideoFrameRangeError = "video.frames_invalid" | "video.range_invalid";

/** Код ошибки диапазона для видео длительностью durationMs или null, если диапазон допустим. */
export function videoFrameRangeError(range: VideoFrameRange, durationMs: number): VideoFrameRangeError | null {
    const { startMs, intervalMs, count } = range;
    if (!Number.isSafeInteger(startMs) || !Number.isSafeInteger(intervalMs) || !Number.isSafeInteger(count)
        || startMs < 0 || intervalMs < 1 || count < 1) {
        return "video.frames_invalid";
    }
    const last = startMs + (count - 1) * intervalMs;
    return !Number.isSafeInteger(last) || last >= durationMs ? "video.range_invalid" : null;
}

/** Начальный диапазон после выбора видео: с 0, шаг 1 с, min(20, max(1, ceil(длительность в секундах))) кадров. */
export function defaultVideoFrameRange(durationMs: number): VideoFrameRange {
    return { startMs: 0, intervalMs: 1000, count: Math.min(20, Math.max(1, Math.ceil(durationMs / 1000))) };
}
