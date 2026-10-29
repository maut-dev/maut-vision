import type { TrainingEpochDto } from "@/api/projects/training/response-dto/training-run-response-dto";
import { trainingMetricLabels } from "@/api/projects/training/training-parameters";

export interface EpochPoint {
    epoch: number;
    value: number;
}

/** A chart series: the color comes from a fixed palette; val losses take the color of their train series and are dashed. */
export interface EpochSeries {
    key: string;
    label: string;
    color: string;
    dashed?: boolean;
    points: EpochPoint[];
}

/** Series colors of the run passport. */
export const seriesColors = ["#60a5fa", "#34d399", "#fbbf24", "#c084fc"] as const;

const qualityKeys = ["precision", "recall", "mAP50", "mAP50_95"] as const;
const lossKeys = ["box_loss", "cls_loss", "dfl_loss", "l1_loss"] as const;
const validationLosses: Readonly<Record<string, string>> = { box_loss: "valBoxLoss", cls_loss: "valClsLoss", dfl_loss: "valDflLoss" };

const label = (key: string) => trainingMetricLabels[key] ?? key;

function pick(epochs: readonly TrainingEpochDto[], read: (epoch: TrainingEpochDto) => number | undefined): EpochPoint[] {
    const points: EpochPoint[] = [];
    for (const entry of epochs) {
        const value = read(entry);
        if (typeof value === "number" && Number.isFinite(value)) points.push({ epoch: entry.epoch, value });
    }
    return points;
}

/** "Quality by epoch": precision, recall, mAP50, mAP50-95 — only those the worker sent. */
export function qualitySeries(epochs: readonly TrainingEpochDto[]): EpochSeries[] {
    return qualityKeys
        .map((key, index) => ({ key, label: label(key), color: seriesColors[index], points: pick(epochs, entry => entry.metrics[key]) }))
        .filter(series => series.points.length > 0);
}

/** "Losses by epoch": train losses as solid lines, the same val losses dashed in the same color. */
export function lossSeries(epochs: readonly TrainingEpochDto[]): EpochSeries[] {
    const result: EpochSeries[] = [];
    lossKeys.forEach((key, index) => {
        const color = seriesColors[index % seriesColors.length];
        const train = pick(epochs, entry => entry.losses[key]);
        if (train.length) result.push({ key, label: label(key), color, points: train });
        const valKey = validationLosses[key];
        const val = valKey ? pick(epochs, entry => entry.metrics[valKey]) : [];
        if (val.length) result.push({ key: valKey, label: label(valKey), color, dashed: true, points: val });
    });
    return result;
}

/**
 * Thinning of a long series for drawing: points are split into buckets by the chart width, each keeps its minimum and maximum
 * (peaks are not lost), the first and the last points stay. A short series returns as is. The tooltip by epoch takes values
 * from the full series.
 */
export function thinPoints(points: readonly EpochPoint[], maxPoints: number): readonly EpochPoint[] {
    if (points.length <= maxPoints || maxPoints < 4) return points;
    const buckets = Math.floor((maxPoints - 2) / 2);
    const inner = points.length - 2;
    const result: EpochPoint[] = [points[0]];
    for (let bucket = 0; bucket < buckets; bucket++) {
        const from = 1 + Math.floor((bucket * inner) / buckets);
        const to = 1 + Math.floor(((bucket + 1) * inner) / buckets);
        if (to <= from) continue;
        let min = from;
        let max = from;
        for (let index = from + 1; index < to; index++) {
            if (points[index].value < points[min].value) min = index;
            if (points[index].value > points[max].value) max = index;
        }
        if (min === max) result.push(points[min]);
        else result.push(points[Math.min(min, max)], points[Math.max(min, max)]);
    }
    result.push(points[points.length - 1]);
    return result;
}

export interface ChartDomain {
    minEpoch: number;
    maxEpoch: number;
    yMin: number;
    yMax: number;
}

/** Axis X — epochs of all series; axis Y — 0..1 for quality, otherwise from 0 (or a minimum below zero) to the maximum plus 8 %. */
export function chartDomain(series: readonly EpochSeries[], unit: boolean): ChartDomain {
    let minEpoch = Infinity;
    let maxEpoch = -Infinity;
    let minValue = 0;
    let maxValue = 1e-9;
    for (const line of series) {
        for (const point of line.points) {
            if (point.epoch < minEpoch) minEpoch = point.epoch;
            if (point.epoch > maxEpoch) maxEpoch = point.epoch;
            if (point.value < minValue) minValue = point.value;
            if (point.value > maxValue) maxValue = point.value;
        }
    }
    if (minEpoch === Infinity) {
        minEpoch = 1;
        maxEpoch = 1;
    }
    return unit ? { minEpoch, maxEpoch, yMin: 0, yMax: 1 } : { minEpoch, maxEpoch, yMin: minValue, yMax: maxValue * 1.08 };
}

/** Even ticks from min to max inclusive. */
export function ticks(min: number, max: number, count: number): number[] {
    if (max <= min || count < 1) return [min];
    const step = (max - min) / count;
    return Array.from({ length: count + 1 }, (_, index) => min + step * index);
}

/** Epoch ticks: a round step 1, 2, 5 · 10ⁿ, at most 8 labels, the first and the last epochs are always labelled. */
export function epochTicks(minEpoch: number, maxEpoch: number): number[] {
    if (maxEpoch <= minEpoch) return [minEpoch];
    const rough = (maxEpoch - minEpoch) / 6;
    const power = 10 ** Math.floor(Math.log10(Math.max(1, rough)));
    const step = [1, 2, 5, 10].map(factor => factor * power).find(candidate => candidate >= rough) ?? 10 * power;
    const result = [minEpoch];
    for (let tick = Math.ceil((minEpoch + step / 2) / step) * step; tick < maxEpoch - step / 2; tick += step) result.push(tick);
    result.push(maxEpoch);
    return result;
}

/** The nearest epoch of a sorted list to the fractional epoch of the pointer. */
export function nearestEpoch(sortedEpochs: readonly number[], target: number): number | null {
    if (!sortedEpochs.length) return null;
    let low = 0;
    let high = sortedEpochs.length - 1;
    while (low < high) {
        const middle = (low + high) >> 1;
        if (sortedEpochs[middle] < target) low = middle + 1;
        else high = middle;
    }
    const after = sortedEpochs[low];
    const before = low > 0 ? sortedEpochs[low - 1] : after;
    return Math.abs(before - target) <= Math.abs(after - target) ? before : after;
}

/** Epochs of all series ascending without repeats. */
export function seriesEpochs(series: readonly EpochSeries[]): number[] {
    return [...new Set(series.flatMap(line => line.points.map(point => point.epoch)))].sort((left, right) => left - right);
}
