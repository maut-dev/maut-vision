import type { JobResponseDto } from "@/api/jobs/response-dto/job-response-dto";
import type { TrainingParameterValue } from "@/api/projects/training/request-dto/training-request-dto";
import type { ModelVersionFileDto } from "@/api/projects/training/response-dto/model-version-response-dto";
import type { TrainingRunResponseDto, TrainingRunState } from "@/api/projects/training/response-dto/training-run-response-dto";
import { trainingParameter } from "@/api/projects/training/training-parameters";
import { i18n } from "@/i18n";
import { jobErrorText, workerErrorText } from "@/lib/errors";
import { formatBytes as formatSize, locale } from "@/lib/format";
import { trainingText } from "@/i18n/sections/training";

export const count = (value: number) => value.toLocaleString(locale());

export function formatNumber(value: number, digits = 4): string {
    return value.toLocaleString(locale(), { maximumFractionDigits: digits });
}

/** A metric of the version passport: three digits, "—" without a value. */
export function formatMetric(value: number | null | undefined): string {
    return typeof value === "number" && Number.isFinite(value) ? value.toFixed(3) : "—";
}

/** Sizes in the units of the interface language (lib/format.ts). */
export function formatBytes(bytes: number): string {
    return formatSize(bytes);
}

export function deviceLabel(device: TrainingParameterValue | undefined): string {
    if (device === "cpu") return i18n.t("training:deviceLabel.cpu");
    if (device === "-1") return i18n.t("training:deviceLabel.anyGpu");
    return i18n.t("training:deviceLabel.gpu", { index: String(device ?? "—") });
}

/**
 * A job snapshot from job.changed newer than the run snapshot: the state follows the job (result, queue, "Cancelling…"); preparing and
 * training differ only by the run — training.changed refines them.
 */
export function withJob(run: TrainingRunResponseDto, job: JobResponseDto): TrainingRunResponseDto {
    if (job.id !== run.id || job.sequence <= run.job.sequence) return run;
    let state: TrainingRunState = run.state;
    if (job.status === "succeeded" || job.status === "failed" || job.status === "cancelled" || job.status === "interrupted") state = job.status;
    else if (job.status === "queued" || job.status === "blocked") state = "queued";
    else if (job.cancelRequested) state = "cancelling";
    else if (state === "queued") state = "preparing";
    return { ...run, job, state };
}

/** The later of two snapshots of one run: by the run revision, the job snapshot by sequence. */
export function newerRun(known: TrainingRunResponseDto | undefined, next: TrainingRunResponseDto): TrainingRunResponseDto {
    if (!known) return next;
    const base = next.revision >= known.revision ? next : known;
    const other = base === next ? known : next;
    return withJob(base, other.job);
}

/** Completion 0–100 for the bar: epochs and batches, otherwise the job progress. */
export function runPercent(run: TrainingRunResponseDto): number | null {
    const { epoch, epochs, batch, batches } = run.progress;
    if (run.state === "succeeded") return 100;
    if (epochs && epoch !== null) {
        const done = batch !== null && batches ? Math.max(0, epoch - 1) + batch / batches : epoch;
        return Math.max(0, Math.min(100, (done / epochs) * 100));
    }
    return run.job.progress;
}

/** The run error for the card and the passport: the text of its code; an interrupted or failed run without a code gets a hint. */
export function runError(run: TrainingRunResponseDto): string | null {
    if (run.state === "cancelled") return null;
    if (run.error) return workerErrorText(run.error);
    if (run.job.error) return jobErrorText(run.job.error);
    if (run.state === "interrupted") return i18n.t("training:runErrors.interrupted");
    if (run.state === "failed") return i18n.t("training:runErrors.failed");
    return null;
}

/** "Fake GPU · Python 3.14.7 · torch 2.14.0 · ultralytics 8.4.157 · CUDA 13.0". */
export function environmentText(environment: Record<string, unknown> | null): string | null {
    if (!environment) return null;
    const parts: string[] = [];
    const text = (key: string) => (typeof environment[key] === "string" && environment[key] ? String(environment[key]) : null);
    if (text("deviceName")) parts.push(text("deviceName")!);
    if (text("python")) parts.push(`Python ${text("python")}`);
    if (text("torch")) parts.push(`torch ${text("torch")}`);
    if (text("ultralytics")) parts.push(`ultralytics ${text("ultralytics")}`);
    if (text("cuda")) parts.push(`CUDA ${text("cuda")}`);
    return parts.length ? parts.join(" · ") : null;
}

const maxValueLength = 160;

/** A parameter value in the list "Run parameters": "Auto" for null, "On" / "Off", long values are shortened. */
export function parameterText(value: unknown): string {
    let text: string;
    if (value === null || value === undefined) text = i18n.t("training:parameterChoices.auto");
    else if (typeof value === "boolean") text = value ? i18n.t("training:parameterChoices.on") : i18n.t("training:parameterChoices.off");
    else if (typeof value === "number") text = formatNumber(value, 6);
    else if (Array.isArray(value) && value.every(item => typeof item !== "object" || item === null)) text = value.join(", ");
    else if (typeof value === "object") text = JSON.stringify(value);
    else text = String(value);
    return text.length > maxValueLength
        ? i18n.t("training:parameterValue.shortened", { text: text.slice(0, maxValueLength), length: count(text.length), count: text.length })
        : text;
}

/** The label of a parameter key: the label of the registry, otherwise the key as is (actual parameters of the worker). */
export function parameterLabel(key: string): string {
    return trainingParameter(key)?.label ?? key;
}

/** Order of images: the most useful first, the rest by name. */
const plotOrder = [
    "results.png",
    "confusion_matrix.png",
    "confusion_matrix_normalized.png",
    "labels.jpg",
    "BoxF1_curve.png",
    "BoxPR_curve.png",
    "BoxP_curve.png",
    "BoxR_curve.png",
];

/** Images with a caption of their own: the file name → a key of plotCaptions in the training catalog. */
const plotCaptions: Readonly<Record<string, string>> = {
    "results.png": "results",
    "confusion_matrix.png": "confusionMatrix",
    "confusion_matrix_normalized.png": "confusionMatrixNormalized",
    "labels.jpg": "labels",
    "labels_correlogram.jpg": "labelsCorrelogram",
    "BoxF1_curve.png": "f1Curve",
    "BoxPR_curve.png": "prCurve",
    "BoxP_curve.png": "pCurve",
    "BoxR_curve.png": "rCurve",
};

export function plotCaption(name: string): string {
    const base = name.split("/").pop() ?? name;
    if (plotCaptions[base]) return trainingText(`plotCaptions.${plotCaptions[base]}`);
    const train = /^train_batch(\d+)\./.exec(base);
    if (train) return i18n.t("training:plotCaptions.trainBatch", { number: train[1] });
    const val = /^val_batch(\d+)_(labels|pred)\./.exec(base);
    if (val) return val[2] === "pred" ? i18n.t("training:plotCaptions.valBatchPred", { number: val[1] }) : i18n.t("training:plotCaptions.valBatchLabels", { number: val[1] });
    if (/curve\.png$/i.test(base)) return i18n.t("training:plotCaptions.curve");
    return base;
}

function plotRank(name: string): number {
    const base = name.split("/").pop() ?? name;
    const index = plotOrder.indexOf(base);
    if (index >= 0) return index;
    if (base.startsWith("train_batch")) return 50;
    if (base.startsWith("val_batch")) return 60;
    return 70;
}

export function sortedPlots(files: readonly ModelVersionFileDto[]): ModelVersionFileDto[] {
    return files.filter(file => file.kind === "plot").sort((left, right) => plotRank(left.name) - plotRank(right.name) || left.name.localeCompare(right.name));
}

/** Labels of version files in the current language (read at render time). */
export const fileKindLabels: Readonly<Record<ModelVersionFileDto["kind"], string>> = {
    get weights_pt() { return i18n.t("training:fileKinds.weights_pt"); },
    get onnx() { return i18n.t("training:fileKinds.onnx"); },
    get plot() { return i18n.t("training:fileKinds.plot"); },
    get log() { return i18n.t("training:fileKinds.log"); },
};
