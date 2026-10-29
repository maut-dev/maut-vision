/**
 * Geometry of annotation boxes. A box is in fractions 0–1 of the oriented frame from the top left corner; every operation returns a
 * box inside the frame, so saving never gets images.box_invalid for crossing the edge.
 */
export interface Box {
    x: number;
    y: number;
    width: number;
    height: number;
}

export interface Point {
    x: number;
    y: number;
}

/** A resize handle: sides and corners. */
export type Handle = "n" | "s" | "e" | "w" | "ne" | "nw" | "se" | "sw";

export const handles: readonly Handle[] = ["nw", "n", "ne", "e", "se", "s", "sw", "w"];

/** A smaller box counts as an accidental click and is not created. */
export const minBoxSize = 0.002;

const clamp01 = (value: number) => Math.min(1, Math.max(0, value));

export function clampPoint(point: Point): Point {
    return { x: clamp01(point.x), y: clamp01(point.y) };
}

/**
 * The pointer point in fractions of the frame. rect is the on-screen rectangle of the shown frame: with zoom and scroll it already
 * includes both the magnification and the shift, so the coordinates do not depend on the view zoom.
 */
export function pointInFrame(clientX: number, clientY: number, rect: { left: number; top: number; width: number; height: number }): Point {
    return clampPoint({
        x: (clientX - rect.left) / Math.max(1e-9, rect.width),
        y: (clientY - rect.top) / Math.max(1e-9, rect.height),
    });
}

export function boxFromPoints(start: Point, end: Point): Box {
    const a = clampPoint(start), b = clampPoint(end);
    return { x: Math.min(a.x, b.x), y: Math.min(a.y, b.y), width: Math.abs(b.x - a.x), height: Math.abs(b.y - a.y) };
}

export function isUsableBox(box: Box): boolean {
    return box.width >= minBoxSize && box.height >= minBoxSize
        && box.x >= 0 && box.y >= 0 && box.x + box.width <= 1 + 1e-9 && box.y + box.height <= 1 + 1e-9;
}

/** Moving a box; the box stops at the frame edge and keeps its size. */
export function moveBox(box: Box, dx: number, dy: number): Box {
    return {
        ...box,
        x: Math.min(1 - box.width, Math.max(0, box.x + dx)),
        y: Math.min(1 - box.height, Math.max(0, box.y + dy)),
    };
}

/**
 * Resizing by a handle: the moved sides follow the pointer, the opposite ones stay in place.
 * A side does not come closer than minBoxSize to the opposite one and does not leave the frame.
 */
export function resizeBox(box: Box, handle: Handle, point: Point): Box {
    const p = clampPoint(point);
    let left = box.x, top = box.y, right = box.x + box.width, bottom = box.y + box.height;
    if (handle.includes("w")) left = Math.min(p.x, right - minBoxSize);
    if (handle.includes("e")) right = Math.max(p.x, left + minBoxSize);
    if (handle.includes("n")) top = Math.min(p.y, bottom - minBoxSize);
    if (handle.includes("s")) bottom = Math.max(p.y, top + minBoxSize);
    left = Math.max(0, left);
    top = Math.max(0, top);
    right = Math.min(1, right);
    bottom = Math.min(1, bottom);
    return { x: left, y: top, width: right - left, height: bottom - top };
}

/** The position of a handle in fractions of the frame. */
export function handlePosition(box: Box, handle: Handle): Point {
    const x = handle.includes("w") ? box.x : handle.includes("e") ? box.x + box.width : box.x + box.width / 2;
    const y = handle.includes("n") ? box.y : handle.includes("s") ? box.y + box.height : box.y + box.height / 2;
    return { x, y };
}

/** A box in frame pixels for the details: "x 10, y 20, width 30, height 40 px". */
export function boxInPixels(box: Box, width: number, height: number) {
    return {
        x: Math.round(box.x * width),
        y: Math.round(box.y * height),
        width: Math.round(box.width * width),
        height: Math.round(box.height * height),
    };
}

export function sameBox(left: Box | null, right: Box | null): boolean {
    if (!left || !right) return left === right;
    return left.x === right.x && left.y === right.y && left.width === right.width && left.height === right.height;
}
