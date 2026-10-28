import { centeredRegion } from "@/api/projects/backgrounds/background-import-rules";
import type { BackgroundRegionRequestDto } from "@/api/projects/backgrounds/request-dto/import-backgrounds-request-dto";
import type { InputSizeDto } from "@/api/projects/input-size-dto";

/** Левый верхний угол рамки в пикселях исходного кадра. */
export type FramePosition = BackgroundRegionRequestDto;

interface Size {
    width: number;
    height: number;
}

/** Последняя взятая область: позиция по умолчанию для следующего файла того же размера. */
export interface LastRegion extends Size {
    at: FramePosition;
}

/** Рамка целиком внутри кадра, координаты — целые пиксели. */
export function clampRegion(position: FramePosition, source: Size, target: InputSizeDto): FramePosition {
    return {
        left: Math.min(Math.max(0, Math.round(position.left)), Math.max(0, source.width - target.width)),
        top: Math.min(Math.max(0, Math.round(position.top)), Math.max(0, source.height - target.height)),
    };
}

/** Позиция по умолчанию: прежнее решение для этого файла → последняя позиция для файла того же размера → центр. */
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

/** Стрелки двигают рамку на 1 px, с Shift — на 10 px; другая клавиша — null. */
export function nudgeRegion(position: FramePosition, key: string, shift: boolean, source: Size, target: InputSizeDto): FramePosition | null {
    const move = arrows[key];
    if (!move) return null;
    const step = shift ? 10 : 1;
    return clampRegion({ left: position.left + move[0] * step, top: position.top + move[1] * step }, source, target);
}

/**
 * Рамка с центром под указателем. Кадр на экране может быть уменьшенной копией, поэтому указатель переводится в пиксели
 * исходного кадра долями экранного прямоугольника.
 */
export function pointerRegion(clientX: number, clientY: number, box: { left: number; top: number; width: number; height: number }, source: Size,
    target: InputSizeDto): FramePosition | null {
    if (!box.width || !box.height) return null;
    const x = ((clientX - box.left) / box.width) * source.width;
    const y = ((clientY - box.top) / box.height) * source.height;
    return clampRegion({ left: x - target.width / 2, top: y - target.height / 2 }, source, target);
}
