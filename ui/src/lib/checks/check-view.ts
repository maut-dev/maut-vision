import type { EvaluationSplit, CheckParametersDto, CheckRegionDto } from "@/api/projects/checks/request-dto/checks-request-dto";
import type { LiveCheckState } from "@/api/projects/checks/response-dto/live-check-response-dto";
import type { ModelEvaluationState } from "@/api/projects/checks/response-dto/model-evaluation-response-dto";
import type { DatasetVersionResponseDto } from "@/api/projects/datasets/response-dto/dataset-response-dto";
import type { InputSizeDto } from "@/api/projects/input-size-dto";
import type { GpuInventoryResponseDto } from "@/api/projects/training/response-dto/training-runtime-response-dto";
import type { ModelVersionResponseDto } from "@/api/projects/training/response-dto/model-version-response-dto";
import { deviceOptions, type TrainingDeviceOption } from "@/api/projects/training/training-parameters";
import { i18n } from "@/i18n";
import "@/i18n/sections/checks";
import type { FramePosition } from "@/lib/backgrounds/crop-frame";
import { errorText } from "@/lib/errors";
import { formatNumber } from "@/lib/format";
import { seriesColor } from "@/lib/series-color";

/** Quality metrics of a version: higher is better, from 0 to 1. */
export const qualityMetrics = ["mAP50", "mAP50_95", "precision", "recall"] as const;
export type QualityMetric = (typeof qualityMetrics)[number];

/**
 * Names and help of the quality metrics in the current language (common: checks, models and analytics show them). Read during render:
 * each property is looked up when it is read, so a language change renders the new text.
 */
export const qualityMetricLabels: Readonly<Record<QualityMetric, string>> = {
    get mAP50() { return i18n.t("metrics.label.mAP50"); },
    get mAP50_95() { return i18n.t("metrics.label.mAP50_95"); },
    get precision() { return i18n.t("metrics.label.precision"); },
    get recall() { return i18n.t("metrics.label.recall"); },
};

export const qualityMetricHelp: Readonly<Record<QualityMetric, string>> = {
    get mAP50() { return i18n.t("metrics.help.mAP50"); },
    get mAP50_95() { return i18n.t("metrics.help.mAP50_95"); },
    get precision() { return i18n.t("metrics.help.precision"); },
    get recall() { return i18n.t("metrics.help.recall"); },
};

/** Split names stay the Ultralytics terms in every language. */
export const splitLabels: Record<EvaluationSplit, string> = { val: "Validation", test: "Test" };

/** The best value of a row; null when there is nothing to compare (fewer than two numbers) or all are equal. */
export function bestOf(values: readonly (number | null | undefined)[]): number | null {
    const numbers = values.filter((value): value is number => typeof value === "number" && Number.isFinite(value));
    if (numbers.length < 2) return null;
    const best = Math.max(...numbers);
    return numbers.every(value => value === best) ? null : best;
}

export const classColor = (classIndex: number) => seriesColor(classIndex + 1);

/** "1:05.3". */
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

/** "Version N", "Version N · working". */
export function versionName(version: { number: number; isWorking: boolean }): string {
    const name = i18n.t("checks:version.number", { number: version.number });
    return version.isWorking ? `${name} · ${i18n.t("checks:version.working")}` : name;
}

/** "Version N · working · mAP50 0.xxx". */
export function versionOptionLabel(version: ModelVersionResponseDto): string {
    const metric = version.metrics.mAP50;
    return typeof metric === "number" ? `${versionName(version)} · mAP50 ${metric.toFixed(3)}` : versionName(version);
}

/** The device of checks: the training list without the automatic choice, a check runs on the named device (Core takes cpu or an index). */
export function checkDeviceOptions(gpus: GpuInventoryResponseDto | null): { options: TrainingDeviceOption[]; manualEntry: boolean } {
    const { options, manualEntry } = deviceOptions(gpus);
    return { options: options.filter(option => option.value !== "-1"), manualEntry };
}

/** Check fields as typed: the threshold, the detection limit (empty is Auto), the device. */
export interface CheckParameterValues {
    confidence: string;
    maxDetections: string;
    device: string;
}

export const defaultCheckParameters: CheckParameterValues = { confidence: "0.25", maxDetections: "", device: "" };

export type CheckParameterField = keyof CheckParameterValues;

/** Request parameters or the first invalid field with its explanation; the same rules as Core. Call during render. */
export function parseCheckParameters(values: CheckParameterValues): { parameters: CheckParametersDto } | { field: CheckParameterField; message: string } {
    const confidence = Number(values.confidence.replace(",", "."));
    if (!values.confidence.trim() || !Number.isFinite(confidence) || confidence < 0 || confidence > 1) {
        return { field: "confidence", message: i18n.t("checks:validation.confidence") };
    }
    const limitText = values.maxDetections.trim();
    const limit = Number(limitText);
    if (limitText && (!Number.isSafeInteger(limit) || limit < 1)) {
        return { field: "maxDetections", message: i18n.t("checks:validation.maxDetections") };
    }
    if (!/^(cpu|\d{1,3})$/.test(values.device)) return { field: "device", message: i18n.t("checks:validation.device") };
    return { parameters: { confidence, maxDetections: limitText ? limit : null, device: values.device } };
}

/** A video or a screen larger than the input on at least one side and not smaller on the other: a frame of the input size is cut out. */
export function needsRegion(source: { width: number; height: number }, input: InputSizeDto): boolean {
    return source.width >= input.width && source.height >= input.height && (source.width > input.width || source.height > input.height);
}

export function toRegion(position: FramePosition, input: InputSizeDto): CheckRegionDto {
    return { x: position.left, y: position.top, width: input.width, height: input.height };
}

/** The step in seconds → ms; null when it is not a number or less than 1 ms. */
export function parseStep(text: string): number | null {
    const seconds = Number(text.replace(",", "."));
    if (!text.trim() || !Number.isFinite(seconds)) return null;
    const ms = Math.round(seconds * 1000);
    return ms >= 1 ? ms : null;
}

/** The hint under the step: how many frames will be checked. */
export function stepEstimate(durationMs: number, stepMs: number): string {
    if (stepMs >= durationMs) return i18n.t("checks:video.stepTooLong");
    const frames = Math.floor((durationMs - 1) / stepMs) + 1;
    return i18n.t("checks:video.estimate", { frames: formatNumber(frames) });
}

/**
 * Compatibility of a dataset with a version as in Core: the same classes on the same outputs (a singleCls version takes a dataset of one of
 * its classes), the frame size equals the input, the split has frames. null when compatible; otherwise the text of the error Core would send.
 */
export function evaluationProblem(version: ModelVersionResponseDto, dataset: DatasetVersionResponseDto, split: EvaluationSplit): string | null {
    const classes = [...version.classes].sort((left, right) => left.index - right.index);
    const datasetClasses = [...dataset.classMap].sort((left, right) => left.index - right.index);
    const merged = classes.length === 1 && classes[0].classId === null;
    const same = merged
        ? datasetClasses.length === 1 && classes[0].sourceClassIds.includes(datasetClasses[0].classId)
        : classes.length === datasetClasses.length && classes.every((entry, index) => entry.index === datasetClasses[index].index && entry.classId === datasetClasses[index].classId);
    if (!same) return errorText("checks.evaluation_classes_mismatch");
    if (dataset.width !== version.input.width || dataset.height !== version.input.height) {
        return errorText("checks.evaluation_input_mismatch", {
            datasetWidth: String(dataset.width), datasetHeight: String(dataset.height),
            versionWidth: String(version.input.width), versionHeight: String(version.input.height),
        });
    }
    if (dataset.counts[split] === 0) return errorText("checks.evaluation_split_empty", { split: splitLabels[split] });
    return null;
}

/** The default split: Test when it has frames. */
export const preferredSplit = (dataset: DatasetVersionResponseDto | undefined): EvaluationSplit => (dataset && dataset.counts.test > 0 ? "test" : "val");
