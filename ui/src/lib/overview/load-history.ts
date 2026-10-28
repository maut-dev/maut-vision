import type { HardwareSampleResponseDto } from "@/api/hardware/response-dto/hardware-sample-response-dto";

/** Окно мини-графика нагрузки. */
export const loadWindowMs = 120_000;

/** Разрыв линии: замеров не было дольше (окно скрыто, ответ задержался). */
export const loadGapMs = 6_000;

/** Точка нагрузки: время замера и проценты CPU и видеокарт по индексу; null — значения нет. */
export interface LoadPoint {
    at: number;
    cpu: number | null;
    gpus: Readonly<Record<number, number | null>>;
}

/** Новый замер в конце истории; точки старше окна и повтор того же замера отбрасываются. Историю хранит страница, пока раздел открыт. */
export function appendLoad(history: readonly LoadPoint[], sample: HardwareSampleResponseDto): readonly LoadPoint[] {
    const at = Date.parse(sample.timestamp);
    if (history.length && history[history.length - 1].at >= at) return history;
    const point: LoadPoint = {
        at,
        cpu: sample.cpu.utilizationPercent,
        gpus: Object.fromEntries(sample.gpus.map(gpu => [gpu.index, gpu.utilizationPercent])),
    };
    return [...history.filter(item => item.at > at - loadWindowMs), point];
}

/** Отрезки линии одной серии: соседние точки дальше loadGapMs или без значения разрывают линию. */
export function loadSegments(history: readonly LoadPoint[], read: (point: LoadPoint) => number | null | undefined): { at: number; value: number }[][] {
    const segments: { at: number; value: number }[][] = [];
    let current: { at: number; value: number }[] = [];
    let previous: number | undefined;
    for (const point of history) {
        const value = read(point);
        if (typeof value !== "number" || (previous !== undefined && point.at - previous > loadGapMs)) {
            if (current.length) segments.push(current);
            current = [];
        }
        if (typeof value === "number") current.push({ at: point.at, value });
        previous = point.at;
    }
    if (current.length) segments.push(current);
    return segments;
}
