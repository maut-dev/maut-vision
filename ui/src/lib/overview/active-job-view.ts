import type { ActiveJobResponseDto } from "@/api/jobs/response-dto/active-jobs-page-response-dto";
import type { CodedTextDto } from "@/api/coded-text-dto";
import { i18n } from "@/i18n";
import "@/i18n/sections/overview";
import { codedText, innerLabel } from "@/lib/coded-text";
import { formatBytes, formatNumber } from "@/lib/format";

/** The project section «Open» of a job leads to; training.run opens its run, checks.evaluate the Dataset mode of checks. */
export type JobSection = "images" | "backgrounds" | "classes" | "generation" | "datasets" | "training" | "models" | "checks";

/** The counter unit of a job: what processed and total count; bytes is a file size. */
export type ActiveJobUnit = "images" | "files" | "frames" | "epochs" | "bytes";

/**
 * A job kind of the running jobs: the section (null when the job has no section and «Open» is not shown) and the counter unit. The label
 * is the text common:jobs.kinds.<kind>.
 */
export interface ActiveJobKind {
    section: JobSection | null;
    unit: ActiveJobUnit | null;
}

/** Map "kind → section, unit"; it lives in the UI, Core does not know the sections. */
export const activeJobKinds: Readonly<Record<string, ActiveJobKind>> = {
    "images.import": { section: "images", unit: "images" },
    "backgrounds.import": { section: "backgrounds", unit: "files" },
    "classes.import_assets": { section: "classes", unit: "files" },
    "generation.run": { section: "generation", unit: "images" },
    "datasets.build": { section: "datasets", unit: "frames" },
    "datasets.verify": { section: "datasets", unit: "files" },
    "datasets.export": { section: "datasets", unit: "frames" },
    "training.run": { section: "training", unit: "epochs" },
    "training.export": { section: "models", unit: "bytes" },
    "checks.evaluate": { section: "checks", unit: null },
    "projects.export": { section: null, unit: "files" },
};

/** Job kind texts are keys known only at run time; the typed t accepts literal keys, so lookups by kind go through this signature. */
const translate = i18n.t.bind(i18n) as unknown as (key: string, options?: Record<string, unknown>) => string;

/** The label of a job kind in the current language; an unknown kind as is. */
export function jobKindLabel(kind: string): string {
    const key = `jobs.kinds.${kind}`;
    return i18n.exists(key) && typeof i18n.t(key as never, { returnObjects: true } as never) === "string" ? translate(key) : kind;
}

/** "Dataset build · Dataset 1"; an unknown kind as is. Call during render. */
export function activeJobTitle(item: ActiveJobResponseDto): string {
    const kind = jobKindLabel(item.job.kind);
    const label = item.job.label ? codedText(item.job.label) : "";
    return label ? `${kind} · ${label}` : kind;
}

/**
 * The state: Cancelling…, a queued job is Queued or Waiting for the GPU, a running one shows its progress text (Preparing, Epoch 3 of 10,
 * Saving the model version), without one Running with the counters "processed / total unit". Call during render.
 */
export function activeJobState(item: ActiveJobResponseDto): string {
    const { job } = item;
    if (job.cancelRequested) return i18n.t("jobs.cancelling");
    if (job.status === "queued") return item.waitingForGpu ? i18n.t("overview:jobs.waitingForGpu") : i18n.t("jobs.status.queued");
    if (job.progressMessage) return codedText(job.progressMessage);
    if (job.processed === null) return i18n.t("overview:jobs.running");
    const unit = activeJobKinds[job.kind]?.unit ?? null;
    const format = unit === "bytes" ? formatBytes : (value: number) => formatNumber(value);
    const progress = job.total !== null ? `${format(job.processed)} / ${format(job.total)}` : format(job.processed);
    return i18n.t(unit && unit !== "bytes" ? `overview:jobs.counter.${unit}` : "overview:jobs.counter.plain", { progress });
}

/** The bar percent: the job progress, otherwise by the counters; null when the amount is unknown (the bar pulses). */
export function activeJobPercent(item: ActiveJobResponseDto): number | null {
    const { job } = item;
    if (job.status === "queued") return 0;
    if (job.progress !== null) return job.progress;
    return job.total ? Math.min(100, ((job.processed ?? 0) / job.total) * 100) : null;
}

/**
 * The version number of a model export label (jobLabels.version): «Open» finds the version by it. A label stored by 1.0.0 is the Russian
 * text "Версия N" (data, matched as stored).
 */
export function exportedVersionNumber(wrapped: CodedTextDto | null): number | null {
    // A job an AI client started carries its label inside jobLabels.ai_label.
    const label = wrapped ? innerLabel(wrapped) : null;
    if (label?.code === "jobLabels.version" && label.args?.number && /^\d+$/.test(label.args.number)) return Number(label.args.number);
    const match = label?.text ? /^Версия (\d+)/.exec(label.text) : null;
    return match ? Number(match[1]) : null;
}
