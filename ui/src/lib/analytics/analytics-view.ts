import type { DatasetVersionResponseDto } from "@/api/projects/datasets/response-dto/dataset-response-dto";
import type { ModelVersionResponseDto } from "@/api/projects/training/response-dto/model-version-response-dto";
import type { TrainingEpochDto } from "@/api/projects/training/response-dto/training-run-response-dto";
import { trainingMetricLabels } from "@/api/projects/training/training-parameters";
import { qualityMetricLabels, qualityMetrics, type QualityMetric } from "@/lib/checks/check-view";
import { deviceLabel, formatMetric, formatNumber } from "@/lib/training/run-view";

/** Версий в сравнении по умолчанию: три последние. */
export const defaultSelectionSize = 3;

/** Пояснения строк таблицы и метрик. */
export const metricHelp: Readonly<Record<string, string>> = {
    mAP50: "mAP50 — средняя точность по классам при IoU ≥ 0.5: основная метрика детекции YOLO. Считается на части Validation датасета.",
    mAP50_95: "mAP50-95 — то же, усреднённое по порогам IoU от 0.5 до 0.95: строже к точности рамок. По ней ultralytics выбирает лучшие веса.",
    precision: "Точность — доля срабатываний, которые оказались верными. Низкая — модель часто видит объект там, где его нет.",
    recall: "Полнота — доля реальных объектов, которые модель нашла. Низкая — модель многое пропускает.",
    bestEpoch: "Лучшая эпоха — эпоха с максимальным fitness ultralytics (сейчас это mAP50-95 на валидации): веса best.pt версии взяты оттуда.",
    epochs: "Эпох пройдено из заданных. Меньше заданного — сработала ранняя остановка (patience): метрики перестали расти.",
    dataset: "Датасет, на котором обучалась версия, и число кадров в частях Train и Validation.",
    lr0: "Начальная скорость обучения (lr0). «подобрано» — работник посчитал её по батчу.",
    batch: "Размер батча. Если ultralytics уменьшил его из-за нехватки памяти, в скобках — фактический.",
    amp: "Смешанная точность (AMP): фактическое значение — на процессоре и части видеокарт работник её отключает.",
};

/** Метрики графика в стандартном порядке; прочие ключи истории идут следом по алфавиту. */
const curveOrder = ["mAP50", "mAP50_95", "precision", "recall", "box_loss", "cls_loss", "dfl_loss", "valBoxLoss", "valClsLoss", "valDflLoss", "fitness", "lr"];

const curveLabels: Readonly<Record<string, string>> = {
    ...qualityMetricLabels,
    box_loss: "Потери рамок",
    cls_loss: "Потери классов",
    dfl_loss: "Потери DFL",
    valBoxLoss: "Потери рамок (val)",
    valClsLoss: "Потери классов (val)",
    valDflLoss: "Потери DFL (val)",
    lr: "Скорость обучения (lr)",
    fitness: "Fitness",
};

export const curveLabel = (key: string) => curveLabels[key] ?? trainingMetricLabels[key] ?? key;

export const isQualityMetric = (key: string) => (qualityMetrics as readonly string[]).includes(key);

/** Значение эпохи для графика: метрика валидации, потеря обучения, lr или fitness. */
export function epochValue(entry: TrainingEpochDto, key: string): number | null {
    if (key === "lr") return entry.lr;
    if (key === "fitness") return entry.fitness;
    return entry.metrics[key] ?? entry.losses[key] ?? null;
}

/** Метрики, которые есть в истории выбранных версий, в стандартном порядке. */
export function curveMetrics(histories: readonly (readonly TrainingEpochDto[])[]): string[] {
    const keys = new Set<string>();
    for (const epochs of histories) {
        for (const entry of epochs) {
            Object.keys(entry.metrics).forEach(key => keys.add(key));
            Object.keys(entry.losses).forEach(key => keys.add(key));
            if (entry.fitness !== null) keys.add("fitness");
            if (entry.lr !== null) keys.add("lr");
        }
    }
    const known = curveOrder.filter(key => keys.has(key));
    return [...known, ...[...keys].filter(key => !known.includes(key)).sort()];
}

/** Строка «Метрики и параметры»: quality — подсветка лучшего и отставание; value — число для сравнения. */
export interface AnalyticsRow {
    key: string;
    label: string;
    help?: string;
    quality?: QualityMetric;
    text: (version: ModelVersionResponseDto) => string;
}

const number = (value: unknown) => (typeof value === "number" && Number.isFinite(value) ? value : null);

/** Строки таблицы; датасет называется по текущему списку: «(в архиве)», отсутствующий — «Датасет удалён». */
export function analyticsRows(datasets: readonly DatasetVersionResponseDto[]): AnalyticsRow[] {
    const datasetName = (version: ModelVersionResponseDto) => {
        const dataset = datasets.find(item => item.id === version.dataset.versionId);
        if (!dataset) return "Датасет удалён";
        return dataset.archivedAt ? `${dataset.name} (в архиве)` : dataset.name;
    };
    return [
        ...qualityMetrics.map<AnalyticsRow>(key => ({
            key,
            label: qualityMetricLabels[key],
            help: metricHelp[key],
            quality: key,
            text: version => formatMetric(version.metrics[key]),
        })),
        { key: "bestEpoch", label: "Лучшая эпоха", help: metricHelp.bestEpoch, text: version => version.bestEpoch === null ? "—" : String(version.bestEpoch) },
        {
            key: "epochs",
            label: "Эпох пройдено / задано",
            help: metricHelp.epochs,
            text: version => `${version.epochsCompleted} / ${number(version.parameters.epochs) ?? "—"}`,
        },
        { key: "dataset", label: "Датасет обучения", help: metricHelp.dataset, text: datasetName },
        { key: "split", label: "Кадров train / val", text: version => `${version.dataset.counts.train} / ${version.dataset.counts.val}` },
        { key: "date", label: "Дата", text: version => new Date(version.createdAt).toLocaleString("ru-RU") },
        {
            key: "batch",
            label: "Batch",
            help: metricHelp.batch,
            text: version => {
                const requested = version.parameters.batch;
                const effective = number(version.effectiveParams.effectiveBatch);
                return effective !== null && effective !== requested ? `${String(requested)} (${effective})` : String(requested ?? "—");
            },
        },
        {
            key: "lr0",
            label: "lr0",
            help: metricHelp.lr0,
            text: version => {
                const value = number(version.effectiveParams.lr0) ?? number(version.parameters.lr0);
                const source = version.effectiveParams.lr0Source;
                return value === null ? "—" : formatNumber(value, 6) + (source === "computed" || source === "optimizer-auto" ? " (подобрано)" : "");
            },
        },
        {
            key: "amp",
            label: "AMP",
            help: metricHelp.amp,
            text: version => {
                const value = version.effectiveParams.amp ?? version.parameters.amp;
                return typeof value === "boolean" ? (value ? "вкл" : "выкл") : "—";
            },
        },
        { key: "device", label: "Устройство", text: version => deviceLabel(version.parameters.device) },
    ];
}

/** «Копировать таблицу»: TSV с заголовком версий, значения как в таблице без отставаний. */
export function analyticsTsv(rows: readonly AnalyticsRow[], versions: readonly ModelVersionResponseDto[]): string {
    const lines = [
        ["Параметр", ...versions.map(version => `Версия ${version.number}`)],
        ...rows.map(row => [row.label, ...versions.map(version => row.text(version))]),
    ];
    return lines.map(line => line.map(cell => cell.replace(/[\t\r\n]+/g, " ")).join("\t")).join("\n");
}

const selectionKey = (projectId: string) => `maut.analytics.selection.${projectId}`;

/** Сохранённый выбор версий проекта; null — выбора ещё не было или хранилище недоступно. */
export function readSelection(projectId: string): string[] | null {
    try {
        const raw = window.localStorage.getItem(selectionKey(projectId));
        const value: unknown = raw ? JSON.parse(raw) : null;
        return Array.isArray(value) && value.every(item => typeof item === "string") ? value : null;
    } catch {
        return null;
    }
}

export function writeSelection(projectId: string, ids: readonly string[]): void {
    try {
        window.localStorage.setItem(selectionKey(projectId), JSON.stringify(ids));
    } catch {
        // Выбор не сохранится между запусками; сравнение работает и без него.
    }
}
