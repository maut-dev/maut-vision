/**
 * Video frames at the marks startMs + i × intervalMs, i = 0…count − 1, with the same rules the host checks
 * (VideoFrameRange.Validate): start ≥ 0, interval ≥ 1 ms, count ≥ 1, otherwise video.frames_invalid; the last mark is
 * strictly before the end of the video, otherwise video.range_invalid.
 */
export interface VideoFrameRange {
    startMs: number;
    intervalMs: number;
    count: number;
}

export type VideoFrameRangeError = "video.frames_invalid" | "video.range_invalid";

/** The range error code for a video of durationMs, or null when the range is valid. */
export function videoFrameRangeError(range: VideoFrameRange, durationMs: number): VideoFrameRangeError | null {
    const { startMs, intervalMs, count } = range;
    if (!Number.isSafeInteger(startMs) || !Number.isSafeInteger(intervalMs) || !Number.isSafeInteger(count)
        || startMs < 0 || intervalMs < 1 || count < 1) {
        return "video.frames_invalid";
    }
    const last = startMs + (count - 1) * intervalMs;
    return !Number.isSafeInteger(last) || last >= durationMs ? "video.range_invalid" : null;
}

/** The initial range after choosing a video: from 0, a step of 1 s, min(20, max(1, ceil(duration in seconds))) frames. */
export function defaultVideoFrameRange(durationMs: number): VideoFrameRange {
    return { startMs: 0, intervalMs: 1000, count: Math.min(20, Math.max(1, Math.ceil(durationMs / 1000))) };
}
