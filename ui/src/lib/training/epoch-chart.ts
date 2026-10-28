import type { TrainingEpochDto } from "@/api/projects/training/response-dto/training-run-response-dto";
import { trainingMetricLabels } from "@/api/projects/training/training-parameters";

export interface EpochPoint {
    epoch: number;
    value: number;
}

/** Серия графика: цвет — из фиксированной палитры; val-потери берут цвет своей train-серии и рисуются пунктиром. */
export interface EpochSeries {
    key: string;
    label: string;
    color: string;
    dashed?: boolean;
    points: EpochPoint[];
}

/** Цвета серий паспорта запуска. */
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

/** «Качество по эпохам»: precision, recall, mAP50, mAP50-95 — только присланные worker. */
export function qualitySeries(epochs: readonly TrainingEpochDto[]): EpochSeries[] {
    return qualityKeys
        .map((key, index) => ({ key, label: label(key), color: seriesColors[index], points: pick(epochs, entry => entry.metrics[key]) }))
        .filter(series => series.points.length > 0);
}

/** «Потери по эпохам»: потери train сплошной линией, те же потери val пунктиром того же цвета. */
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
 * Прореживание длинной серии для отрисовки: точки делятся на корзины по ширине графика, в каждой остаются минимум и максимум
 * (пики не теряются), первая и последняя точки сохраняются. Короткая серия возвращается как есть. Подсказка по эпохе берёт
 * значения из полной серии.
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

/** Ось X — эпохи всех серий; ось Y — 0..1 для качества, иначе от 0 (или минимума ниже нуля) до максимума с запасом 8 %. */
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

/** Равномерные деления от min до max включительно. */
export function ticks(min: number, max: number, count: number): number[] {
    if (max <= min || count < 1) return [min];
    const step = (max - min) / count;
    return Array.from({ length: count + 1 }, (_, index) => min + step * index);
}

/** Деления эпох: круглый шаг 1, 2, 5 · 10ⁿ, не больше 8 подписей, первая и последняя эпохи всегда подписаны. */
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

/** Ближайшая эпоха из отсортированного списка к дробной эпохе указателя. */
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

/** Эпохи всех серий по возрастанию без повторов. */
export function seriesEpochs(series: readonly EpochSeries[]): number[] {
    return [...new Set(series.flatMap(line => line.points.map(point => point.epoch)))].sort((left, right) => left - right);
}
