/**
 * Геометрия рамок разметки. Рамка — доли 0–1 ориентированного кадра от левого верхнего угла; все операции возвращают
 * рамку внутри кадра, поэтому сохранение не получает images.box_invalid из-за выхода за край.
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

/** Маркер изменения размера: стороны и углы. */
export type Handle = "n" | "s" | "e" | "w" | "ne" | "nw" | "se" | "sw";

export const handles: readonly Handle[] = ["nw", "n", "ne", "e", "se", "s", "sw", "w"];

/** Меньшая рамка считается случайным щелчком и не создаётся. */
export const minBoxSize = 0.002;

const clamp01 = (value: number) => Math.min(1, Math.max(0, value));

export function clampPoint(point: Point): Point {
    return { x: clamp01(point.x), y: clamp01(point.y) };
}

/**
 * Точка указателя в долях кадра. rect — экранный прямоугольник показанного кадра: при масштабе и прокрутке он уже
 * учитывает и увеличение, и сдвиг, поэтому координаты не зависят от масштаба просмотра.
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

/** Сдвиг рамки; рамка упирается в край кадра и сохраняет размер. */
export function moveBox(box: Box, dx: number, dy: number): Box {
    return {
        ...box,
        x: Math.min(1 - box.width, Math.max(0, box.x + dx)),
        y: Math.min(1 - box.height, Math.max(0, box.y + dy)),
    };
}

/**
 * Изменение размера за маркер: перемещаемые стороны идут за указателем, противоположные остаются на месте.
 * Сторона не проходит через противоположную ближе minBoxSize и не выходит за кадр.
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

/** Положение маркера в долях кадра. */
export function handlePosition(box: Box, handle: Handle): Point {
    const x = handle.includes("w") ? box.x : handle.includes("e") ? box.x + box.width : box.x + box.width / 2;
    const y = handle.includes("n") ? box.y : handle.includes("s") ? box.y + box.height : box.y + box.height / 2;
    return { x, y };
}

/** Рамка в пикселях кадра для подробностей: «x 10, y 20, ширина 30, высота 40 px». */
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
