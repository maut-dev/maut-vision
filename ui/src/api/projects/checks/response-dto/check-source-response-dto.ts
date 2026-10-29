import { isResourceUrl, type ResourceUrl } from "../../../resources/resource-url";
import { isRecord } from "../../../transport/envelopes";
import { isCount } from "../../catalog/catalog-guards";

/** The chosen image: token is the choice (null — the picker was closed); the path does not reach the UI. */
export interface CheckFileSelectionResponseDto {
    token: string | null;
    name: string | null;
    byteSize: number | null;
}

/** A video frame: the size of the video frame (the crop is placed in its coordinates) and of the scaled-down preview at url. */
export interface CheckVideoFrameResponseDto {
    timestampMs: number;
    width: number;
    height: number;
    previewWidth: number;
    previewHeight: number;
    url: ResourceUrl;
}

/** The chosen video: the frame size, the duration, the frame rate and a frame from the middle (Show another frame is previewVideoFrame). */
export interface CheckVideoSelectionResponseDto {
    token: string | null;
    name: string | null;
    byteSize: number | null;
    width: number | null;
    height: number | null;
    durationMs: number | null;
    fps: number | null;
    frame: CheckVideoFrameResponseDto | null;
}

/** A monitor in physical pixels: "Monitor index" (width × height); index is the number for the screen check. */
export interface MonitorResponseDto {
    index: number;
    left: number;
    top: number;
    width: number;
    height: number;
    isPrimary: boolean;
    name: string;
}

/** A screenshot of a monitor (PNG at url, in physical pixels) and all monitors at the moment of the screenshot. */
export interface ScreenShotResponseDto {
    monitor: MonitorResponseDto;
    url: ResourceUrl;
    monitors: MonitorResponseDto[];
}

export function isCheckFileSelectionResponseDto(value: unknown): value is CheckFileSelectionResponseDto {
    return isRecord(value) && (value.token === null
        ? value.name === null && value.byteSize === null
        : typeof value.token === "string" && typeof value.name === "string" && isCount(value.byteSize));
}

export function isCheckVideoFrameResponseDto(value: unknown): value is CheckVideoFrameResponseDto {
    return isRecord(value) && isCount(value.timestampMs) && isCount(value.width) && isCount(value.height) && isCount(value.previewWidth)
        && isCount(value.previewHeight) && isResourceUrl(value.url);
}

export function isCheckVideoSelectionResponseDto(value: unknown): value is CheckVideoSelectionResponseDto {
    if (!isRecord(value)) return false;
    if (value.token === null) return value.name === null && value.frame === null;
    return typeof value.token === "string" && typeof value.name === "string" && isCount(value.byteSize) && isCount(value.width) && isCount(value.height)
        && isCount(value.durationMs) && (value.fps === null || typeof value.fps === "number") && isCheckVideoFrameResponseDto(value.frame);
}

export function isMonitorResponseDto(value: unknown): value is MonitorResponseDto {
    return isRecord(value) && isCount(value.index) && Number.isSafeInteger(value.left) && Number.isSafeInteger(value.top) && isCount(value.width)
        && isCount(value.height) && typeof value.isPrimary === "boolean" && typeof value.name === "string";
}

export function isMonitorList(value: unknown): value is MonitorResponseDto[] {
    return Array.isArray(value) && value.every(isMonitorResponseDto);
}

export function isScreenShotResponseDto(value: unknown): value is ScreenShotResponseDto {
    return isRecord(value) && isMonitorResponseDto(value.monitor) && isResourceUrl(value.url) && isMonitorList(value.monitors);
}
