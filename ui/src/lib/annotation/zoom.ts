/**
 * The view zoom of a frame, shared by the viewer and the annotation editor. "Fit" shrinks a large frame and does not enlarge a small
 * one; x1 is one frame pixel per CSS pixel; steps x1…x10. When the zoom changes, the center of the visible area stays on the same
 * point of the frame.
 */
export type ZoomMode = "fit" | number;

export const maxZoom = 10;

export interface Size {
    width: number;
    height: number;
}

export function fitScale(image: Size, viewport: Size): number {
    return Math.min(1, viewport.width / Math.max(1, image.width), viewport.height / Math.max(1, image.height));
}

export function scaleOf(mode: ZoomMode, image: Size, viewport: Size): number {
    return mode === "fit" ? fitScale(image, viewport) : mode;
}

/** Zoom in: floor(scale) + 1, at most x10. */
export function zoomIn(scale: number): ZoomMode {
    return Math.min(maxZoom, Math.max(1, Math.floor(scale) + 1));
}

/** Zoom out: "Fit" at scale ≤ 1, otherwise ceil(scale) − 1. */
export function zoomOut(scale: number): ZoomMode {
    return scale <= 1 ? "fit" : Math.max(1, Math.ceil(scale) - 1);
}

/** The offset of the frame inside the canvas: a frame smaller than the view area is centered. */
export function frameOffset(viewport: number, frame: number): number {
    return Math.max(0, (viewport - frame) / 2);
}

/** The point of the frame (in its pixels) at the center of the view area. */
export function viewCenter(scroll: { left: number; top: number }, viewport: Size, image: Size, scale: number) {
    return {
        x: (scroll.left + viewport.width / 2 - frameOffset(viewport.width, image.width * scale)) / scale,
        y: (scroll.top + viewport.height / 2 - frameOffset(viewport.height, image.height * scale)) / scale,
    };
}

/** The scroll that puts the frame point center at the center of the view area at the zoom scale. */
export function scrollForCenter(center: { x: number; y: number }, viewport: Size, image: Size, scale: number) {
    return {
        left: Math.max(0, center.x * scale + frameOffset(viewport.width, image.width * scale) - viewport.width / 2),
        top: Math.max(0, center.y * scale + frameOffset(viewport.height, image.height * scale) - viewport.height / 2),
    };
}
