import { useMemo, useState } from "react";
import type { ModelVersionResponseDto } from "@/api/projects/training/response-dto/model-version-response-dto";
import type { TrainingEpochDto } from "@/api/projects/training/response-dto/training-run-response-dto";
import { TrainingEpochChart } from "@/components/training/training-epoch-chart";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { curveLabel, curveMetrics, epochValue, isQualityMetric } from "@/lib/analytics/analytics-view";
import { seriesColor } from "@/lib/checks/check-view";
import type { EpochPoint, EpochSeries } from "@/lib/training/epoch-chart";

/** История эпох версии: undefined — загружается, "failed" — не загрузилась. */
export type EpochHistory = readonly TrainingEpochDto[] | "failed" | undefined;

/**
 * «Кривые обучения по эпохам»: одна метрика для всех выбранных версий на общем графике (TrainingEpochChart: наведение «Эпоха N»,
 * легенда с переключением, прореживание thinPoints). Цвет версии — --series-N по её номеру. В списке «Метрика графика» — только
 * метрики, которые есть в истории.
 */
export function AnalyticsCurves({ versions, histories }: {
    versions: readonly ModelVersionResponseDto[];
    /** История по runId версии. */
    histories: ReadonlyMap<string, EpochHistory>;
}) {
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
        return { key: version.id, label: `Версия ${version.number}`, color: seriesColor(version.number), points };
    }).filter(line => line.points.length > 0), [loaded, active]);
    const loading = entries.some(item => item.epochs === undefined);
    const missing = entries.filter(item => item.epochs === "failed" || (Array.isArray(item.epochs) && item.epochs.length === 0));

    return (
        <section className="space-y-3 rounded-lg border bg-card p-5" data-testid="analytics-curves" data-metric={active}>
            <div className="flex flex-wrap items-center justify-between gap-3">
                <h2 className="font-medium">Кривые обучения по эпохам</h2>
                <Select value={active} onValueChange={setMetric} disabled={!available.length}>
                    <SelectTrigger aria-label="Метрика графика" className="w-56" data-testid="analytics-curve-metric"><SelectValue /></SelectTrigger>
                    <SelectContent>
                        {(available.length ? available : ["mAP50"]).map(key => <SelectItem key={key} value={key}>{curveLabel(key)}</SelectItem>)}
                    </SelectContent>
                </Select>
            </div>
            {series.length ? (
                <TrainingEpochChart id="analytics" label={`${curveLabel(active)} по эпохам`} series={series} unit={isQualityMetric(active)} />
            ) : (
                <div className="flex min-h-40 items-center justify-center rounded-md border border-dashed p-6 text-center text-sm text-muted-foreground" data-testid="analytics-curves-empty">
                    {loading ? "Загрузка истории эпох…" : `У выбранных версий нет значений «${curveLabel(active)}» по эпохам.`}
                </div>
            )}
            {missing.length > 0 && (
                <ul className="flex flex-wrap gap-x-5 gap-y-1 text-xs" aria-label="Версии без кривых" data-testid="analytics-curves-missing">
                    {missing.map(({ version, epochs }) => (
                        <li key={version.id} className="flex items-center gap-1.5" data-version-id={version.id}>
                            <span className="inline-block h-0.5 w-4 rounded" style={{ background: seriesColor(version.number) }} />
                            Версия {version.number}
                            <span className="text-muted-foreground">{epochs === "failed" ? "— не удалось загрузить историю эпох" : "— работник не прислал эпох"}</span>
                        </li>
                    ))}
                </ul>
            )}
        </section>
    );
}
