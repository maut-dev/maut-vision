import type { DatasetVersionResponseDto } from "@/api/projects/datasets/response-dto/dataset-response-dto";
import type { ModelVersionResponseDto } from "@/api/projects/training/response-dto/model-version-response-dto";
import type { TrainingEpochDto } from "@/api/projects/training/response-dto/training-run-response-dto";
import { trainingMetricLabels } from "@/api/projects/training/training-parameters";
import { i18n } from "@/i18n";
import "@/i18n/sections/analytics";
import { qualityMetricLabels, qualityMetrics, type QualityMetric } from "@/lib/checks/check-view";
import { deviceLabel, formatNumber } from "@/lib/training/run-view";
import { formatDateTime, formatMetric } from "@/lib/format";

/** Versions compared by default: the latest three. */
export const defaultSelectionSize = 3;

/** Rows of the table that carry an explanation besides the quality metrics. */
type HelpKey = QualityMetric | "bestEpoch" | "epochs" | "dataset" | "lr0" | "batch" | "amp";

/** The explanation of a table row in the current language. Call during render. */
export const metricHelp = (key: HelpKey): string => i18n.t(`analytics:help.${key}`);

/** Chart metrics in the standard order; other history keys follow alphabetically. */
const curveOrder = ["mAP50", "mAP50_95", "precision", "recall", "box_loss", "cls_loss", "dfl_loss", "valBoxLoss", "valClsLoss", "valDflLoss", "fitness", "lr"];

const curveKeys = ["box_loss", "cls_loss", "dfl_loss", "valBoxLoss", "valClsLoss", "valDflLoss", "lr", "fitness"] as const;

export const isQualityMetric = (key: string) => (qualityMetrics as readonly string[]).includes(key);

/** The name of a chart metric in the current language; an unknown key as the training section names it, or as is. Call during render. */
export function curveLabel(key: string): string {
    if (isQualityMetric(key)) return qualityMetricLabels[key as QualityMetric];
    const curve = curveKeys.find(item => item === key);
    return curve ? i18n.t(`analytics:curve.${curve}`) : trainingMetricLabels[key] ?? key;
}

/** The value of an epoch for the chart: a validation metric, a training loss, lr or fitness. */
export function epochValue(entry: TrainingEpochDto, key: string): number | null {
    if (key === "lr") return entry.lr;
    if (key === "fitness") return entry.fitness;
    return entry.metrics[key] ?? entry.losses[key] ?? null;
}

/** Metrics present in the history of the chosen versions, in the standard order. */
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

/** A row of the metrics and parameters: quality marks the best value and the gaps. */
export interface AnalyticsRow {
    key: string;
    label: string;
    help?: string;
    quality?: QualityMetric;
    text: (version: ModelVersionResponseDto) => string;
}

const number = (value: unknown) => (typeof value === "number" && Number.isFinite(value) ? value : null);

/**
 * Rows of the table in the current language (build them during render); a dataset is named by the current list: archived ones are marked,
 * a missing one is "Dataset deleted".
 */
export function analyticsRows(datasets: readonly DatasetVersionResponseDto[]): AnalyticsRow[] {
    const t = i18n.t.bind(i18n);
    const datasetName = (version: ModelVersionResponseDto) => {
        const dataset = datasets.find(item => item.id === version.dataset.versionId);
        if (!dataset) return t("analytics:row.datasetDeleted");
        return dataset.archivedAt ? t("analytics:row.datasetArchived", { name: dataset.name }) : dataset.name;
    };
    return [
        ...qualityMetrics.map<AnalyticsRow>(key => ({
            key,
            label: qualityMetricLabels[key],
            help: metricHelp(key),
            quality: key,
            text: version => formatMetric(version.metrics[key]),
        })),
        { key: "bestEpoch", label: t("analytics:row.bestEpoch"), help: metricHelp("bestEpoch"), text: version => version.bestEpoch === null ? "—" : String(version.bestEpoch) },
        {
            key: "epochs",
            label: t("analytics:row.epochs"),
            help: metricHelp("epochs"),
            text: version => `${version.epochsCompleted} / ${number(version.parameters.epochs) ?? "—"}`,
        },
        { key: "dataset", label: t("analytics:row.dataset"), help: metricHelp("dataset"), text: datasetName },
        { key: "split", label: t("analytics:row.split"), text: version => `${version.dataset.counts.train} / ${version.dataset.counts.val}` },
        { key: "date", label: t("analytics:row.date"), text: version => formatDateTime(version.createdAt) },
        {
            key: "batch",
            label: t("analytics:row.batch"),
            help: metricHelp("batch"),
            text: version => {
                const requested = version.parameters.batch;
                const effective = number(version.effectiveParams.effectiveBatch);
                return effective !== null && effective !== requested ? `${String(requested)} (${effective})` : String(requested ?? "—");
            },
        },
        {
            key: "lr0",
            label: t("analytics:row.lr0"),
            help: metricHelp("lr0"),
            text: version => {
                const value = number(version.effectiveParams.lr0) ?? number(version.parameters.lr0);
                const source = version.effectiveParams.lr0Source;
                if (value === null) return "—";
                const text = formatNumber(value, 6);
                return source === "computed" || source === "optimizer-auto" ? t("analytics:row.tuned", { value: text }) : text;
            },
        },
        {
            key: "amp",
            label: t("analytics:row.amp"),
            help: metricHelp("amp"),
            text: version => {
                const value = version.effectiveParams.amp ?? version.parameters.amp;
                return typeof value === "boolean" ? (value ? t("analytics:row.on") : t("analytics:row.off")) : "—";
            },
        },
        { key: "device", label: t("analytics:row.device"), text: version => deviceLabel(version.parameters.device) },
    ];
}

/** Copy the table: TSV with a header of versions, the values as in the table without the gaps. */
export function analyticsTsv(rows: readonly AnalyticsRow[], versions: readonly ModelVersionResponseDto[]): string {
    const lines = [
        [i18n.t("analytics:table.parameter"), ...versions.map(version => i18n.t("analytics:table.version", { number: version.number }))],
        ...rows.map(row => [row.label, ...versions.map(version => row.text(version))]),
    ];
    return lines.map(line => line.map(cell => cell.replace(/[\t\r\n]+/g, " ")).join("\t")).join("\n");
}

const selectionKey = (projectId: string) => `maut.analytics.selection.${projectId}`;

/** The saved choice of versions of a project; null when there was no choice yet or the storage is unavailable. */
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
        // The choice is not kept between runs; the comparison works without it.
    }
}
