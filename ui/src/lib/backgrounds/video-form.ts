import { availableVideoFrames, defaultVideoImport, videoImportError } from "@/api/projects/backgrounds/background-import-rules";
import type { BackgroundRegionRequestDto } from "@/api/projects/backgrounds/request-dto/import-backgrounds-request-dto";
import type { InputSizeDto } from "@/api/projects/input-size-dto";
import "@/components/backgrounds/locale";
import { i18n } from "@/i18n";

/** The "Cut from video" fields in seconds as the user typed them; the request gets milliseconds. */
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

/** "Up to K frames are available with this step."; 0 when the fields are wrong. */
export function videoFramesAvailable(video: VideoInfo, range: VideoRange): number {
    return availableVideoFrames(video.durationMs, range.startMs, range.intervalMs);
}

/**
 * The validation message before sending, or null. The rule is videoImportError, the same as the host's; the text says which field is
 * wrong.
 */
export function videoFormMessage(video: VideoInfo, target: InputSizeDto, region: BackgroundRegionRequestDto, fields: VideoFields): string | null {
    const range = videoRangeOf(fields);
    const code = videoImportError(video, target, { region, ...range });
    const seconds = (video.durationMs / 1000).toFixed(1);
    switch (code) {
        case null:
            return null;
        case "backgrounds.video_too_small":
            return i18n.t("backgrounds:video.errors.tooSmall", { width: video.width, height: video.height, targetWidth: target.width, targetHeight: target.height });
        case "backgrounds.region_invalid":
            return i18n.t("backgrounds:video.errors.region");
        case "video.frames_invalid":
            return !Number.isSafeInteger(range.intervalMs) || range.intervalMs < 1
                ? i18n.t("backgrounds:video.errors.interval")
                : i18n.t("backgrounds:video.errors.start", { seconds });
        case "video.range_invalid":
            return i18n.t("backgrounds:video.errors.start", { seconds });
        case "backgrounds.count_invalid": {
            if (!Number.isSafeInteger(range.count) || range.count < 1) return i18n.t("backgrounds:video.errors.count");
            return i18n.t("backgrounds:video.errors.tooMany", { count: videoFramesAvailable(video, range), interval: fields.interval });
        }
    }
}
