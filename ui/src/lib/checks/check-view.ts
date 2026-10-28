import type { EvaluationSplit, CheckParametersDto, CheckRegionDto } from "@/api/projects/checks/request-dto/checks-request-dto";
import type { LiveCheckState } from "@/api/projects/checks/response-dto/live-check-response-dto";
import type { ModelEvaluationState } from "@/api/projects/checks/response-dto/model-evaluation-response-dto";
import type { DatasetVersionResponseDto } from "@/api/projects/datasets/response-dto/dataset-response-dto";
import type { InputSizeDto } from "@/api/projects/input-size-dto";
import type { GpuInventoryResponseDto } from "@/api/projects/training/response-dto/training-runtime-response-dto";
import type { ModelVersionResponseDto } from "@/api/projects/training/response-dto/model-version-response-dto";
import { deviceOptions, type TrainingDeviceOption } from "@/api/projects/training/training-parameters";
import type { FramePosition } from "@/lib/backgrounds/crop-frame";
import { locale } from "@/lib/format";

/** Метрики качества версии: больше — лучше, от 0 до 1. */
export const qualityMetrics = ["mAP50", "mAP50_95", "precision", "recall"] as const;
export type QualityMetric = (typeof qualityMetrics)[number];

export const qualityMetricLabels: Record<QualityMetric, string> = {
    mAP50: "mAP50",
    mAP50_95: "mAP50-95",
    precision: "Точность (P)",
    recall: "Полнота (R)",
};

export const qualityMetricHelp: Record<QualityMetric, string> = {
    mAP50: "mAP50 — средняя точность по классам при IoU ≥ 0.5: основная метрика детекции YOLO.",
    mAP50_95: "mAP50-95 — то же, усреднённое по порогам IoU от 0.5 до 0.95: строже к точности рамок. По ней ultralytics выбирает лучшие веса.",
    precision: "Точность — доля срабатываний, которые оказались верными. Низкая — модель часто видит объект там, где его нет.",
    recall: "Полнота — доля реальных объектов, которые модель нашла. Низкая — модель многое пропускает.",
};

export const splitLabels: Record<EvaluationSplit, string> = { val: "Validation", test: "Test" };

/** Лучшее значение строки; null — сравнивать не с чем (меньше двух чисел) или все равны. */
export function bestOf(values: readonly (number | null | undefined)[]): number | null {
    const numbers = values.filter((value): value is number => typeof value === "number" && Number.isFinite(value));
    if (numbers.length < 2) return null;
    const best = Math.max(...numbers);
    return numbers.every(value => value === best) ? null : best;
}

/** Цвет серии --series-N: версия — по номеру (снятие галочки не перекрашивает остальные), класс — по индексу выхода. */
export function seriesColor(position: number): string {
    return `var(--series-${((Math.max(1, position) - 1) % 8) + 1})`;
}

export const classColor = (classIndex: number) => seriesColor(classIndex + 1);

/** «1:05.3». */
export function formatTime(ms: number | null | undefined): string {
    if (typeof ms !== "number" || !Number.isFinite(ms)) return "—";
    const total = ms / 1000;
    const minutes = Math.floor(total / 60);
    const seconds = total - minutes * 60;
    return `${minutes}:${seconds.toFixed(1).padStart(4, "0")}`;
}

export const formatConfidence = (value: number) => `${Math.round(value * 100)}%`;

export const isLiveCheckActive = (state: LiveCheckState) => state === "preparing" || state === "running" || state === "cancelling";

export const isEvaluationActive = (state: ModelEvaluationState) => state === "queued" || state === "preparing" || state === "running" || state === "cancelling";

/** «Версия N · рабочая · mAP50 0.xxx». */
export function versionOptionLabel(version: ModelVersionResponseDto): string {
    const metric = version.metrics.mAP50;
    return `Версия ${version.number}${version.isWorking ? " · рабочая" : ""}${typeof metric === "number" ? ` · mAP50 ${metric.toFixed(3)}` : ""}`;
}

/** «Устройство» проверок: список обучения без автовыбора — проверка идёт на названном устройстве (Core принимает cpu или номер). */
export function checkDeviceOptions(gpus: GpuInventoryResponseDto | null): { options: TrainingDeviceOption[]; manualEntry: boolean } {
    const { options, manualEntry } = deviceOptions(gpus);
    return { options: options.filter(option => option.value !== "-1"), manualEntry };
}

/** Поля проверки как набраны: порог, предел обнаружений (пусто — «Автоматически»), устройство. */
export interface CheckParameterValues {
    confidence: string;
    maxDetections: string;
    device: string;
}

export const defaultCheckParameters: CheckParameterValues = { confidence: "0.25", maxDetections: "", device: "" };

export type CheckParameterField = keyof CheckParameterValues;

/** Параметры запроса или первое неверное поле с объяснением — те же правила, что у Core. */
export function parseCheckParameters(values: CheckParameterValues): { parameters: CheckParametersDto } | { field: CheckParameterField; message: string } {
    const confidence = Number(values.confidence.replace(",", "."));
    if (!values.confidence.trim() || !Number.isFinite(confidence) || confidence < 0 || confidence > 1) {
        return { field: "confidence", message: "Порог уверенности — число от 0 до 1." };
    }
    const limitText = values.maxDetections.trim();
    const limit = Number(limitText);
    if (limitText && (!Number.isSafeInteger(limit) || limit < 1)) {
        return { field: "maxDetections", message: "Число обнаружений — целое положительное число или пусто («Автоматически»)." };
    }
    if (!/^(cpu|\d{1,3})$/.test(values.device)) return { field: "device", message: "Устройство — «Процессор» или номер видеокарты." };
    return { parameters: { confidence, maxDetections: limitText ? limit : null, device: values.device } };
}

/** Видео или экран больше входа хотя бы по одной стороне и не меньше по другой: из кадра вырезается рамка входа. */
export function needsRegion(source: { width: number; height: number }, input: InputSizeDto): boolean {
    return source.width >= input.width && source.height >= input.height && (source.width > input.width || source.height > input.height);
}

export function toRegion(position: FramePosition, input: InputSizeDto): CheckRegionDto {
    return { x: position.left, y: position.top, width: input.width, height: input.height };
}

/** «Шаг, с» → мс; null — не число или меньше 1 мс. */
export function parseStep(text: string): number | null {
    const seconds = Number(text.replace(",", "."));
    if (!text.trim() || !Number.isFinite(seconds)) return null;
    const ms = Math.round(seconds * 1000);
    return ms >= 1 ? ms : null;
}

/** Подсказка под шагом: сколько кадров проверится. */
export function stepEstimate(durationMs: number, stepMs: number): string {
    if (stepMs >= durationMs) return "Шаг больше длительности видео: будет проверен только первый кадр.";
    const frames = Math.floor((durationMs - 1) / stepMs) + 1;
    return `Будет проверено около ${frames.toLocaleString(locale())} кадров.`;
}

/**
 * Совместимость датасета с версией так же, как в Core: те же классы на тех же выходах (версия singleCls — датасет из одного её
 * класса), размер кадров равен входу, в части есть кадры. null — совместим.
 */
export function evaluationProblem(version: ModelVersionResponseDto, dataset: DatasetVersionResponseDto, split: EvaluationSplit): string | null {
    const classes = [...version.classes].sort((left, right) => left.index - right.index);
    const datasetClasses = [...dataset.classMap].sort((left, right) => left.index - right.index);
    const merged = classes.length === 1 && classes[0].classId === null;
    const same = merged
        ? datasetClasses.length === 1 && classes[0].sourceClassIds.includes(datasetClasses[0].classId)
        : classes.length === datasetClasses.length && classes.every((entry, index) => entry.index === datasetClasses[index].index && entry.classId === datasetClasses[index].classId);
    if (!same) return "Классы датасета не совпадают с классами версии (состав или порядок): проверка на нём бессмысленна.";
    if (dataset.width !== version.input.width || dataset.height !== version.input.height) {
        return `Размер кадров датасета ${dataset.width} × ${dataset.height} не совпадает со входом версии ${version.input.width} × ${version.input.height}.`;
    }
    if (dataset.counts[split] === 0) return `В части ${splitLabels[split]} этого датасета нет кадров.`;
    return null;
}

/** Часть по умолчанию: Test, если в ней есть кадры. */
export const preferredSplit = (dataset: DatasetVersionResponseDto | undefined): EvaluationSplit => (dataset && dataset.counts.test > 0 ? "test" : "val");
