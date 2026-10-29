import type { HardwareSampleResponseDto } from "@/api/hardware/response-dto/hardware-sample-response-dto";

/** The window of the load mini chart. */
export const loadWindowMs = 120_000;

/** A line break: there were no samples for longer (the window was hidden, an answer was late). */
export const loadGapMs = 6_000;

/** A load point: the sample time and the percents of the CPU and of the GPUs by index; null when there is no value. */
export interface LoadPoint {
    at: number;
    cpu: number | null;
    gpus: Readonly<Record<number, number | null>>;
}

/** A new sample at the end of the history; points older than the window and a repeat of the same sample are dropped. The page keeps the history while the section is open. */
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

/** Line segments of one series: neighbor points further apart than loadGapMs, or without a value, break the line. */
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
