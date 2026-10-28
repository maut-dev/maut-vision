import type { ActiveJobResponseDto } from "@/api/jobs/response-dto/active-jobs-page-response-dto";
import type { CodedTextDto } from "@/api/coded-text-dto";
import { codedText } from "@/lib/coded-text";
import { formatBytes } from "@/lib/training/run-view";
import { locale } from "@/lib/format";

/** Раздел проекта, куда ведёт «Открыть» у задачи; training.run открывает свой запуск, checks.evaluate — режим «Датасет». */
export type JobSection = "images" | "backgrounds" | "classes" | "generation" | "datasets" | "training" | "models" | "checks";

/** Вид задачи для «Сейчас выполняется»: подпись, раздел (null — у задачи нет раздела, «Открыть» не показывается) и единица счётчиков (bytes — размер файла). */
export interface ActiveJobKind {
    label: string;
    section: JobSection | null;
    unit: string | null;
}

/** Карта «вид → раздел, единица»; хранится в UI, Core о разделах не знает. */
export const activeJobKinds: Readonly<Record<string, ActiveJobKind>> = {
    "images.import": { label: "Импорт изображений", section: "images", unit: "изображений" },
    "backgrounds.import": { label: "Подготовка фонов", section: "backgrounds", unit: "файлов" },
    "classes.import_assets": { label: "Импорт материалов", section: "classes", unit: "файлов" },
    "generation.run": { label: "Генерация", section: "generation", unit: "изображений" },
    "datasets.build": { label: "Сборка датасета", section: "datasets", unit: "кадров" },
    "datasets.verify": { label: "Проверка датасета", section: "datasets", unit: "файлов" },
    "datasets.export": { label: "Экспорт датасета", section: "datasets", unit: "кадров" },
    "training.run": { label: "Обучение", section: "training", unit: "эпох" },
    "training.export": { label: "Выгрузка модели", section: "models", unit: "bytes" },
    "checks.evaluate": { label: "Проверка модели", section: "checks", unit: null },
    "projects.export": { label: "Экспорт проекта", section: null, unit: "файлов" },
};

const count = (value: number) => value.toLocaleString(locale());

/** «Сборка датасета · Датасет 1»; неизвестный вид — как есть. */
export function activeJobTitle(item: ActiveJobResponseDto): string {
    const kind = activeJobKinds[item.job.kind]?.label ?? item.job.kind;
    const label = item.job.label ? codedText(item.job.label) : "";
    return label ? `${kind} · ${label}` : kind;
}

/**
 * Состояние: «Отмена…», в очереди — «В очереди» или «Ожидает видеокарту», идущая — текст прогресса задачи («Подготовка», «Эпоха 3 из
 * 10», «Сохранение версии модели»), без него — «Выполняется» со счётчиками «processed / total единица».
 */
export function activeJobState(item: ActiveJobResponseDto): string {
    const { job } = item;
    if (job.cancelRequested) return "Отмена…";
    if (job.status === "queued") return item.waitingForGpu ? "Ожидает видеокарту" : "В очереди";
    if (job.progressMessage) return codedText(job.progressMessage);
    if (job.processed === null) return "Выполняется";
    const unit = activeJobKinds[job.kind]?.unit ?? null;
    if (unit === "bytes") return `Выполняется · ${formatBytes(job.processed)}${job.total !== null ? ` / ${formatBytes(job.total)}` : ""}`;
    return `Выполняется · ${count(job.processed)}${job.total !== null ? ` / ${count(job.total)}` : ""}${unit ? ` ${unit}` : ""}`;
}

/** Процент полосы: progress задачи, иначе по счётчикам; null — объём неизвестен (полоса пульсирует). */
export function activeJobPercent(item: ActiveJobResponseDto): number | null {
    const { job } = item;
    if (job.status === "queued") return 0;
    if (job.progress !== null) return job.progress;
    return job.total ? Math.min(100, ((job.processed ?? 0) / job.total) * 100) : null;
}

/** The version number of a model export label (jobLabels.version; a label of 1.0.0 is the text «Версия N»): «Открыть» finds the version by it. */
export function exportedVersionNumber(label: CodedTextDto | null): number | null {
    if (label?.code === "jobLabels.version" && label.args?.number && /^\d+$/.test(label.args.number)) return Number(label.args.number);
    const match = label?.text ? /^Версия (\d+)/.exec(label.text) : null;
    return match ? Number(match[1]) : null;
}
