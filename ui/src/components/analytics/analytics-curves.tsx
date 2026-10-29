import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import type { ModelVersionResponseDto } from "@/api/projects/training/response-dto/model-version-response-dto";
import type { TrainingEpochDto } from "@/api/projects/training/response-dto/training-run-response-dto";
import { TrainingEpochChart } from "@/components/training/training-epoch-chart";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { curveLabel, curveMetrics, epochValue, isQualityMetric } from "@/lib/analytics/analytics-view";
import type { EpochPoint, EpochSeries } from "@/lib/training/epoch-chart";
import { seriesColor } from "@/lib/series-color";

/** The epoch history of a version: undefined while it loads, "failed" when it did not load. */
export type EpochHistory = readonly TrainingEpochDto[] | "failed" | undefined;

/**
 * Training curves by epoch: one metric for all chosen versions on a shared chart (TrainingEpochChart: hover shows the epoch, a legend
 * with toggles, thinning by thinPoints). A version's color is --series-N by its number. The chart metric list holds only metrics present
 * in the history.
 */
export function AnalyticsCurves({ versions, histories }: {
    versions: readonly ModelVersionResponseDto[];
    /** The history by the runId of a version. */
    histories: ReadonlyMap<string, EpochHistory>;
}) {
    const { t } = useTranslation("analytics");
    const [metric, setMetric] = useState("mAP50");
    const entries = useMemo(() => versions.map(version => ({ version, epochs: histories.get(version.runId) })), [versions, histories]);
    const loaded = useMemo(() => entries.filter((item): item is { version: ModelVersionResponseDto; epochs: readonly TrainingEpochDto[] } => Array.isArray(item.epochs)), [entries]);
    const available = useMemo(() => curveMetrics(loaded.map(item => item.epochs)), [loaded]);
    const active = available.includes(metric) ? metric : (available[0] ?? "mAP50");
    const series = useMemo<EpochSeries[]>(() => loaded.map(({ version, epochs }) => {
        const points: EpochPoint[] = [];
        for (const entry of epochs) {
            const value = epochValue(entry, active);
            if (typeof value === "number" && Number.isFinite(value)) points.push({ epoch: entry.epoch, value });
        }
        return { key: version.id, label: t("table.version", { number: version.number }), color: seriesColor(version.number), points };
    }).filter(line => line.points.length > 0), [loaded, active, t]);
    const loading = entries.some(item => item.epochs === undefined);
    const missing = entries.filter(item => item.epochs === "failed" || (Array.isArray(item.epochs) && item.epochs.length === 0));

    return (
        <section className="space-y-3 rounded-lg border bg-card p-5" data-testid="analytics-curves" data-metric={active}>
            <div className="flex flex-wrap items-center justify-between gap-3">
                <h2 className="font-medium">{t("curves.title")}</h2>
                <Select value={active} onValueChange={setMetric} disabled={!available.length}>
                    <SelectTrigger aria-label={t("curves.metricAria")} className="w-56" data-testid="analytics-curve-metric"><SelectValue /></SelectTrigger>
                    <SelectContent>
                        {(available.length ? available : ["mAP50"]).map(key => <SelectItem key={key} value={key}>{curveLabel(key)}</SelectItem>)}
                    </SelectContent>
                </Select>
            </div>
            {series.length ? (
                <TrainingEpochChart id="analytics" label={t("curves.chart", { metric: curveLabel(active) })} series={series} unit={isQualityMetric(active)} />
            ) : (
                <div className="flex min-h-40 items-center justify-center rounded-md border border-dashed p-6 text-center text-sm text-muted-foreground" data-testid="analytics-curves-empty">
                    {loading ? t("curves.loading") : t("curves.noValues", { metric: curveLabel(active) })}
                </div>
            )}
            {missing.length > 0 && (
                <ul className="flex flex-wrap gap-x-5 gap-y-1 text-xs" aria-label={t("curves.missingAria")} data-testid="analytics-curves-missing">
                    {missing.map(({ version, epochs }) => (
                        <li key={version.id} className="flex items-center gap-1.5" data-version-id={version.id}>
                            <span className="inline-block h-0.5 w-4 rounded" style={{ background: seriesColor(version.number) }} />
                            {t("table.version", { number: version.number })}
                            <span className="text-muted-foreground">{epochs === "failed" ? t("curves.failed") : t("curves.noEpochs")}</span>
                        </li>
                    ))}
                </ul>
            )}
        </section>
    );
}
