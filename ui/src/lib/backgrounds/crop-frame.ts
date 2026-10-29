import { centeredRegion } from "@/api/projects/backgrounds/background-import-rules";
import type { BackgroundRegionRequestDto } from "@/api/projects/backgrounds/request-dto/import-backgrounds-request-dto";
import type { InputSizeDto } from "@/api/projects/input-size-dto";

/** The top left corner of the frame in pixels of the source image. */
export type FramePosition = BackgroundRegionRequestDto;

interface Size {
    width: number;
    height: number;
}

/** The last taken region: the default position for the next file of the same size. */
export interface LastRegion extends Size {
    at: FramePosition;
}

/** The frame entirely inside the image, coordinates in whole pixels. */
export function clampRegion(position: FramePosition, source: Size, target: InputSizeDto): FramePosition {
    return {
        left: Math.min(Math.max(0, Math.round(position.left)), Math.max(0, source.width - target.width)),
        top: Math.min(Math.max(0, Math.round(position.top)), Math.max(0, source.height - target.height)),
    };
}

/** The default position: the earlier decision for this file, then the last position for a file of the same size, then the center. */
export function defaultRegion(source: Size, target: InputSizeDto, previous?: FramePosition, last?: LastRegion): FramePosition {
    if (previous) return clampRegion(previous, source, target);
    if (last && last.width === source.width && last.height === source.height) return clampRegion(last.at, source, target);
    return centeredRegion(source.width, source.height, target);
}

const arrows: Readonly<Record<string, readonly [number, number]>> = {
    ArrowLeft: [-1, 0],
    ArrowRight: [1, 0],
    ArrowUp: [0, -1],
    ArrowDown: [0, 1],
};

/** Arrows move the frame by 1 px, with Shift by 10 px; another key gives null. */
export function nudgeRegion(position: FramePosition, key: string, shift: boolean, source: Size, target: InputSizeDto): FramePosition | null {
    const move = arrows[key];
    if (!move) return null;
    const step = shift ? 10 : 1;
    return clampRegion({ left: position.left + move[0] * step, top: position.top + move[1] * step }, source, target);
}

/**
 * The frame centered under the pointer. The image on screen may be a reduced copy, so the pointer is converted to pixels of the
 * source image by fractions of the on-screen rectangle.
 */
export function pointerRegion(clientX: number, clientY: number, box: { left: number; top: number; width: number; height: number }, source: Size,
    target: InputSizeDto): FramePosition | null {
    if (!box.width || !box.height) return null;
    const x = ((clientX - box.left) / box.width) * source.width;
    const y = ((clientY - box.top) / box.height) * source.height;
    return clampRegion({ left: x - target.width / 2, top: y - target.height / 2 }, source, target);
}
