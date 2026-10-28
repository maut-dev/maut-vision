import type { JobResponseDto } from "@/api/jobs/response-dto/job-response-dto";
import type { TrainingParameterValue } from "@/api/projects/training/request-dto/training-request-dto";
import type { ModelVersionFileDto } from "@/api/projects/training/response-dto/model-version-response-dto";
import type { TrainingRunResponseDto, TrainingRunState } from "@/api/projects/training/response-dto/training-run-response-dto";
import { trainingParameter } from "@/api/projects/training/training-parameters";

export const count = (value: number) => value.toLocaleString("ru-RU");

export function formatNumber(value: number, digits = 4): string {
    return value.toLocaleString("ru-RU", { maximumFractionDigits: digits });
}

/** Метрика паспорта версии: три знака, «—» без значения. */
export function formatMetric(value: number | null | undefined): string {
    return typeof value === "number" && Number.isFinite(value) ? value.toFixed(3) : "—";
}

export function formatBytes(bytes: number): string {
    if (bytes < 1024) return `${bytes} Б`;
    if (bytes < 1024 * 1024) return `${formatNumber(bytes / 1024, 1)} КБ`;
    if (bytes < 1024 * 1024 * 1024) return `${formatNumber(bytes / 1024 / 1024, 1)} МБ`;
    return `${formatNumber(bytes / 1024 / 1024 / 1024, 2)} ГБ`;
}

export function deviceLabel(device: TrainingParameterValue | undefined): string {
    if (device === "cpu") return "процессор";
    if (device === "-1") return "свободная видеокарта";
    return `видеокарта ${String(device ?? "—")}`;
}

/**
 * Снимок задачи из job.changed новее снимка запуска: состояние следует за задачей (итог, очередь, «Отмена…»), подготовка и
 * обучение различаются только по запуску — их уточнит training.changed.
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

/** Из двух снимков одного запуска — более поздний: по ревизии запуска, снимок задачи — по sequence. */
export function newerRun(known: TrainingRunResponseDto | undefined, next: TrainingRunResponseDto): TrainingRunResponseDto {
    if (!known) return next;
    const base = next.revision >= known.revision ? next : known;
    const other = base === next ? known : next;
    return withJob(base, other.job);
}

/** Доля выполнения 0–100 для полосы: эпохи и пакеты, иначе прогресс задачи. */
export function runPercent(run: TrainingRunResponseDto): number | null {
    const { epoch, epochs, batch, batches } = run.progress;
    if (run.state === "succeeded") return 100;
    if (epochs && epoch !== null) {
        const done = batch !== null && batches ? Math.max(0, epoch - 1) + batch / batches : epoch;
        return Math.max(0, Math.min(100, (done / epochs) * 100));
    }
    return run.job.progress;
}

/** Ошибка запуска для карточки и паспорта: текст host, для прерванного без кода — подсказка. */
export function runError(run: TrainingRunResponseDto): string | null {
    if (run.state === "cancelled") return null;
    if (run.error) return run.error.message;
    if (run.job.error) return run.job.error.message;
    if (run.state === "interrupted") return "Запуск прерван. Запустите обучение заново.";
    if (run.state === "failed") return "Обучение завершилось ошибкой. Проверьте журнал, датасет и параметры перед повторным запуском.";
    return null;
}

/** «Поддельная видеокарта · Python 3.14.7 · torch 2.14.0 · ultralytics 8.4.157 · CUDA 13.0». */
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

/** Значение параметра в списке «Параметры запуска»: «Авто» для null, «Вкл» / «Выкл», длинные значения сокращаются. */
export function parameterText(value: unknown): string {
    let text: string;
    if (value === null || value === undefined) text = "Авто";
    else if (typeof value === "boolean") text = value ? "Вкл" : "Выкл";
    else if (typeof value === "number") text = formatNumber(value, 6);
    else if (Array.isArray(value) && value.every(item => typeof item !== "object" || item === null)) text = value.join(", ");
    else if (typeof value === "object") text = JSON.stringify(value);
    else text = String(value);
    return text.length > maxValueLength ? `${text.slice(0, maxValueLength)}… (${count(text.length)} символов)` : text;
}

/** Подпись ключа параметра: русская подпись реестра, иначе ключ как есть (фактические параметры worker). */
export function parameterLabel(key: string): string {
    return trainingParameter(key)?.label ?? key;
}

/** Порядок картинок: сначала самые полезные, остальные по имени. */
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

const plotCaptions: Readonly<Record<string, string>> = {
    "results.png": "сводные графики ultralytics",
    "confusion_matrix.png": "матрица ошибок",
    "confusion_matrix_normalized.png": "матрица ошибок (нормированная)",
    "labels.jpg": "распределение рамок",
    "labels_correlogram.jpg": "корреляции рамок",
    "BoxF1_curve.png": "F1 от порога уверенности",
    "BoxPR_curve.png": "кривая точность–полнота",
    "BoxP_curve.png": "точность от порога уверенности",
    "BoxR_curve.png": "полнота от порога уверенности",
};

export function plotCaption(name: string): string {
    const base = name.split("/").pop() ?? name;
    if (plotCaptions[base]) return plotCaptions[base];
    const train = /^train_batch(\d+)\./.exec(base);
    if (train) return `батч обучения ${train[1]}`;
    const val = /^val_batch(\d+)_(labels|pred)\./.exec(base);
    if (val) return `батч проверки ${val[1]} · ${val[2] === "pred" ? "предсказания" : "разметка"}`;
    if (/curve\.png$/i.test(base)) return "кривая качества";
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

export const fileKindLabels: Readonly<Record<ModelVersionFileDto["kind"], string>> = {
    weights_pt: "веса .pt",
    onnx: "веса .onnx",
    plot: "график",
    log: "журнал обучения",
};
