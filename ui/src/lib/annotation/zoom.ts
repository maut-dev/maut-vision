/**
 * Масштаб просмотра кадра, общий для просмотрщика и редактора разметки. «Вписать» уменьшает большой кадр и не
 * увеличивает маленький; x1 — пиксель кадра на CSS-пиксель; шаги x1…x10. При смене масштаба центр видимой области
 * остаётся на той же точке кадра.
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

/** Увеличение: floor(scale) + 1, не больше x10. */
export function zoomIn(scale: number): ZoomMode {
    return Math.min(maxZoom, Math.max(1, Math.floor(scale) + 1));
}

/** Уменьшение: при scale ≤ 1 — «Вписать», иначе ceil(scale) − 1. */
export function zoomOut(scale: number): ZoomMode {
    return scale <= 1 ? "fit" : Math.max(1, Math.ceil(scale) - 1);
}

/** Отступ кадра внутри холста: кадр меньше области просмотра стоит по центру. */
export function frameOffset(viewport: number, frame: number): number {
    return Math.max(0, (viewport - frame) / 2);
}

/** Точка кадра (в его пикселях) в центре области просмотра. */
export function viewCenter(scroll: { left: number; top: number }, viewport: Size, image: Size, scale: number) {
    return {
        x: (scroll.left + viewport.width / 2 - frameOffset(viewport.width, image.width * scale)) / scale,
        y: (scroll.top + viewport.height / 2 - frameOffset(viewport.height, image.height * scale)) / scale,
    };
}

/** Прокрутка, при которой точка кадра center окажется в центре области просмотра при масштабе scale. */
export function scrollForCenter(center: { x: number; y: number }, viewport: Size, image: Size, scale: number) {
    return {
        left: Math.max(0, center.x * scale + frameOffset(viewport.width, image.width * scale) - viewport.width / 2),
        top: Math.max(0, center.y * scale + frameOffset(viewport.height, image.height * scale) - viewport.height / 2),
    };
}
