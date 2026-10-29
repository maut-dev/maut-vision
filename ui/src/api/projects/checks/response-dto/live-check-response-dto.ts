import { isResourceUrl, type ResourceUrl } from "../../../resources/resource-url";
import { isRecord } from "../../../transport/envelopes";
import { isCount, isDate, isDateOrNull, isId } from "../../catalog/catalog-guards";
import type { CheckRegionDto } from "../request-dto/checks-request-dto";
import { isCheckErrorDto, type CheckErrorDto } from "./model-evaluation-response-dto";
import { isCodedTextDto, type CodedTextDto } from "../../../coded-text-dto";

export type LiveCheckMode = "image" | "video" | "screen";

/**
 * State of a live check; the UI names it by the key checks:liveState.<state>. A live check does not wait for the GPU: a busy one is refused
 * at once with checks.gpu_busy_*.
 */
export type LiveCheckState = "preparing" | "running" | "cancelling" | "succeeded" | "failed" | "cancelled";

/** Who stopped it: the button, switching to another project, a page reload or closing the window, the screen time limit (then Done). */
export type LiveCheckStopReason = "user" | "project_switched" | "session_ended" | "time_limit";

export interface LiveCheckSettingsDto {
    confidence: number;
    maxDetections: number | null;
    device: string;
    imageSize: number;
    region: CheckRegionDto | null;
    stepMs: number | null;
    monitor: number | null;
    fps: number | null;
    maxDurationSec: number | null;
}

/** What is checked: the file name (null for the screen), the frame size, the video duration and frame rate, the monitor. */
export interface LiveCheckSourceDto {
    name: string | null;
    width: number | null;
    height: number | null;
    durationMs: number | null;
    fps: number | null;
    monitor: number | null;
}

/**
 * Video: "m:ss.s of m:ss.s · frames checked framesProcessed of ~framesTotal"; screen: "Frames N · fps · elapsedMs elapsed";
 * firstFrameMs — from the worker start to the first frame.
 */
export interface LiveCheckProgressDto {
    framesProcessed: number;
    framesTotal: number | null;
    positionMs: number | null;
    durationMs: number | null;
    fps: number | null;
    elapsedMs: number;
    firstFrameMs: number | null;
}

/** A row of the per-class summary; the class color is --series-N by classIndex. */
export interface LiveCheckClassSummaryDto {
    classIndex: number;
    classId: string | null;
    name: string;
    frames: number;
    detections: number;
    maxConfidence: number;
}

export interface LiveCheckSummaryDto {
    framesProcessed: number;
    framesWithDetections: number;
    detections: number;
    classes: LiveCheckClassSummaryDto[];
}

/** A detection: the box in fractions of the analyzed frame (over the preview without conversion), the caption "name NN%". */
export interface LiveDetectionDto {
    classIndex: number;
    classId: string | null;
    name: string;
    confidence: number;
    x: number;
    y: number;
    width: number;
    height: number;
}

/**
 * A frame: ordinal — the journal record (0 for the last frame without detections), index — the frame number, timeMs — its time; in events
 * detections holds at most the 100 most confident ones, detectionCount counts all.
 */
export interface LiveCheckFrameDto {
    ordinal: number;
    index: number;
    timeMs: number;
    width: number;
    height: number;
    inferenceMs: number;
    detections: LiveDetectionDto[];
    detectionCount: number;
}

/** The latest preview (JPEG); number grows with each new preview. */
export interface LiveCheckPreviewDto {
    number: number;
    frameIndex: number;
    timeMs: number;
    width: number;
    height: number;
    url: ResourceUrl;
}

/** total — frames with detections in all; kept — stored (screen: the last 2,000, video: the first 5,000); truncated — the video journal is cut. */
export interface LiveCheckJournalInfoDto {
    total: number;
    kept: number;
    truncated: boolean;
    firstOrdinal: number;
    lastOrdinal: number;
}

export interface LiveCheckJournalPageResponseDto {
    items: LiveCheckFrameDto[];
    journal: LiveCheckJournalInfoDto;
}

/** A warning of the worker: code for the log; message — a code of the catalog worker with values and the English fallback. */
export interface LiveCheckWarningDto {
    code: string;
    message: CodedTextDto;
}

/**
 * A live check. Snapshots (checksApi.get, list, start*) and checks.changed events (without recentFrames): apply them only when sequence
 * is greater than the known one. lastFrame is what is on screen now; new journal records after an event — checksApi.listJournal({after: lastOrdinal}).
 */
export interface LiveCheckResponseDto {
    id: string;
    mode: LiveCheckMode;
    state: LiveCheckState;
    versionId: string;
    versionNumber: number;
    settings: LiveCheckSettingsDto;
    source: LiveCheckSourceDto;
    progress: LiveCheckProgressDto;
    summary: LiveCheckSummaryDto;
    lastFrame: LiveCheckFrameDto | null;
    preview: LiveCheckPreviewDto | null;
    journal: LiveCheckJournalInfoDto;
    recentFrames: LiveCheckFrameDto[];
    warnings: LiveCheckWarningDto[];
    error: CheckErrorDto | null;
    stopReason: LiveCheckStopReason | null;
    startedAt: string;
    finishedAt: string | null;
    sequence: number;
}

const modes: readonly string[] = ["image", "video", "screen"] satisfies LiveCheckMode[];
const states: readonly string[] = ["preparing", "running", "cancelling", "succeeded", "failed", "cancelled"] satisfies LiveCheckState[];
const reasons: readonly string[] = ["user", "project_switched", "session_ended", "time_limit"] satisfies LiveCheckStopReason[];

const isNumber = (value: unknown): value is number => typeof value === "number" && Number.isFinite(value);
const isNumberOrNull = (value: unknown) => value === null || isNumber(value);
const isCountOrNull = (value: unknown) => value === null || isCount(value);
const isFraction = (value: unknown) => isNumber(value) && value >= 0 && value <= 1;

export function isCheckRegionDto(value: unknown): value is CheckRegionDto {
    return isRecord(value) && isCount(value.x) && isCount(value.y) && isCount(value.width) && isCount(value.height);
}

function isDetection(value: unknown): value is LiveDetectionDto {
    return isRecord(value) && isCount(value.classIndex) && (value.classId === null || isId(value.classId)) && typeof value.name === "string"
        && isFraction(value.confidence) && isFraction(value.x) && isFraction(value.y) && isFraction(value.width) && isFraction(value.height);
}

export function isLiveCheckFrameDto(value: unknown): value is LiveCheckFrameDto {
    return isRecord(value) && isCount(value.ordinal) && isCount(value.index) && isCount(value.timeMs) && isCount(value.width) && isCount(value.height)
        && isCount(value.inferenceMs) && Array.isArray(value.detections) && value.detections.every(isDetection) && isCount(value.detectionCount);
}

export function isLiveCheckJournalInfoDto(value: unknown): value is LiveCheckJournalInfoDto {
    return isRecord(value) && isCount(value.total) && isCount(value.kept) && typeof value.truncated === "boolean" && isCount(value.firstOrdinal)
        && isCount(value.lastOrdinal);
}

export function isLiveCheckResponseDto(value: unknown): value is LiveCheckResponseDto {
    if (!isRecord(value) || !isRecord(value.settings) || !isRecord(value.source) || !isRecord(value.progress) || !isRecord(value.summary)) return false;
    const { settings, source, progress, summary } = value;
    return isId(value.id) && typeof value.mode === "string" && modes.includes(value.mode) && typeof value.state === "string" && states.includes(value.state)
        && isId(value.versionId) && isCount(value.versionNumber)
        && isFraction(settings.confidence) && isCountOrNull(settings.maxDetections) && typeof settings.device === "string" && isCount(settings.imageSize)
        && (settings.region === null || isCheckRegionDto(settings.region)) && isCountOrNull(settings.stepMs) && isCountOrNull(settings.monitor)
        && isNumberOrNull(settings.fps) && isCountOrNull(settings.maxDurationSec)
        && (source.name === null || typeof source.name === "string") && isCountOrNull(source.width) && isCountOrNull(source.height)
        && isCountOrNull(source.durationMs) && isNumberOrNull(source.fps) && isCountOrNull(source.monitor)
        && isCount(progress.framesProcessed) && isCountOrNull(progress.framesTotal) && isCountOrNull(progress.positionMs) && isCountOrNull(progress.durationMs)
        && isNumberOrNull(progress.fps) && isCount(progress.elapsedMs) && isCountOrNull(progress.firstFrameMs)
        && isCount(summary.framesProcessed) && isCount(summary.framesWithDetections) && isCount(summary.detections) && Array.isArray(summary.classes)
        && summary.classes.every(item => isRecord(item) && isCount(item.classIndex) && (item.classId === null || isId(item.classId)) && typeof item.name === "string"
            && isCount(item.frames) && isCount(item.detections) && isFraction(item.maxConfidence))
        && (value.lastFrame === null || isLiveCheckFrameDto(value.lastFrame))
        && (value.preview === null || (isRecord(value.preview) && isCount(value.preview.number) && isCount(value.preview.frameIndex) && isCount(value.preview.timeMs)
            && isCount(value.preview.width) && isCount(value.preview.height) && isResourceUrl(value.preview.url)))
        && isLiveCheckJournalInfoDto(value.journal) && Array.isArray(value.recentFrames) && value.recentFrames.every(isLiveCheckFrameDto)
        && Array.isArray(value.warnings) && value.warnings.every(item => isRecord(item) && typeof item.code === "string" && isCodedTextDto(item.message))
        && (value.error === null || isCheckErrorDto(value.error)) && (value.stopReason === null || (typeof value.stopReason === "string" && reasons.includes(value.stopReason)))
        && isDate(value.startedAt) && isDateOrNull(value.finishedAt) && isCount(value.sequence);
}

export function isLiveCheckList(value: unknown): value is LiveCheckResponseDto[] {
    return Array.isArray(value) && value.every(isLiveCheckResponseDto);
}

export function isLiveCheckJournalPageResponseDto(value: unknown): value is LiveCheckJournalPageResponseDto {
    return isRecord(value) && Array.isArray(value.items) && value.items.every(isLiveCheckFrameDto) && isLiveCheckJournalInfoDto(value.journal);
}
