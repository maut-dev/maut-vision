import { useEffect, useMemo, useRef, useState } from "react";
import { Check, CircleHelp, Copy } from "lucide-react";
import type { ProjectResponseDto } from "@/api/projects/response-dto/project-response-dto";
import { ModelEvaluations } from "@/components/checks/model-evaluations";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { analyticsRows, analyticsTsv, defaultSelectionSize, readSelection, writeSelection } from "@/lib/analytics/analytics-view";
import { bestOf, seriesColor } from "@/lib/checks/check-view";
import { readEpochHistory } from "@/lib/training/epoch-history";
import { useDatasetVersions } from "@/lib/use-dataset-versions";
import { useGpuInventory } from "@/lib/use-gpu-inventory";
import { useModelEvaluations } from "@/lib/use-model-evaluations";
import { useModelVersions } from "@/lib/use-model-versions";
import { AnalyticsCurves, type EpochHistory } from "./analytics-curves";
import { locale } from "@/lib/format";

/**
 * Раздел «Аналитика»: сравнение выбранных версий модели — «Метрики и параметры», «Кривые обучения по эпохам» и «Проверки на
 * датасетах» (общий ModelEvaluations). Без опроса: training.changed, datasets.changed, checks.changed, job.changed. Выбор версий
 * хранится для проекта в localStorage; история эпох читается один раз на версию, пока раздел открыт.
 */
export function AnalyticsSection({ project, openTraining, openPassport }: {
    project: ProjectResponseDto;
    openTraining: () => void;
    openPassport: (versionId: string) => void;
}) {
    const projectId = project.id;
    const models = useModelVersions(projectId);
    const datasets = useDatasetVersions(projectId);
    const evaluations = useModelEvaluations(projectId);
    const gpus = useGpuInventory();
    const [selectedIds, setSelectedIds] = useState<readonly string[]>();
    const [histories, setHistories] = useState<ReadonlyMap<string, EpochHistory>>(new Map());
    const [copied, setCopied] = useState(false);
    const [copyError, setCopyError] = useState<string>();
    const requested = useRef(new Set<string>());
    const mounted = useRef(true);
    const versions = useMemo(() => [...models.versions].sort((left, right) => right.number - left.number), [models.versions]);
    const selected = useMemo(() => versions.filter(item => selectedIds?.includes(item.id)), [versions, selectedIds]);
    const rows = useMemo(() => analyticsRows(datasets.versions), [datasets.versions]);

    useEffect(() => {
        mounted.current = true;
        return () => { mounted.current = false; };
    }, []);

    useEffect(() => {
        // Первый выбор — сохранённый или три последние версии; удалённые версии убираются из выбора.
        if (!models.loaded) return;
        setSelectedIds(current => {
            const base = current ?? readSelection(projectId) ?? versions.slice(0, defaultSelectionSize).map(item => item.id);
            const next = base.filter(id => versions.some(item => item.id === id));
            return current && next.length === current.length ? current : next;
        });
    }, [models.loaded, versions, projectId]);

    useEffect(() => {
        if (selectedIds) writeSelection(projectId, selectedIds);
    }, [projectId, selectedIds]);

    useEffect(() => {
        for (const version of selected) {
            const runId = version.runId;
            if (requested.current.has(runId)) continue;
            requested.current.add(runId);
            readEpochHistory(projectId, runId, () => mounted.current).then(epochs => {
                if (epochs) setHistories(current => new Map(current).set(runId, epochs));
            }, () => {
                requested.current.delete(runId);
                if (mounted.current) setHistories(current => new Map(current).set(runId, "failed"));
            });
        }
    }, [projectId, selected]);

    function toggle(id: string, on: boolean) {
        setSelectedIds(current => on ? [...new Set([...(current ?? []), id])] : (current ?? []).filter(item => item !== id));
    }

    async function copyTable() {
        setCopyError(undefined);
        try {
            await navigator.clipboard.writeText(analyticsTsv(rows, selected));
        } catch {
            setCopyError("Не удалось скопировать таблицу в буфер обмена.");
            return;
        }
        if (!mounted.current) return;
        setCopied(true);
        window.setTimeout(() => { if (mounted.current) setCopied(false); }, 2000);
    }

    if (models.loaded && !versions.length) {
        return (
            <section className="space-y-3 rounded-lg border border-dashed p-6" data-testid="analytics-empty">
                <h2 className="font-medium">Сравнивать пока нечего</h2>
                <p className="max-w-prose text-sm text-muted-foreground">
                    Аналитика сравнивает версии модели проекта: метрики, кривые обучения и проверки на датасетах. Версия появляется после успешного обучения.
                </p>
                <Button variant="outline" data-testid="analytics-open-training" onClick={openTraining}>Открыть обучение</Button>
            </section>
        );
    }

    return (
        <div className="space-y-5" data-testid="analytics-section">
            {models.error && (
                <Alert variant="destructive">
                    <AlertDescription>
                        <p>{models.error}</p>
                        <Button variant="ghost" size="sm" className="mt-1" onClick={() => void models.refresh()}>Повторить</Button>
                    </AlertDescription>
                </Alert>
            )}
            <section className="space-y-3 rounded-lg border bg-card p-5" data-testid="analytics-versions">
                <h2 className="font-medium">Версии для сравнения</h2>
                {!models.loaded ? (
                    <p role="status" className="text-sm text-muted-foreground">Загрузка версий…</p>
                ) : (
                    <div className="flex flex-wrap gap-x-5 gap-y-2">
                        {versions.map(version => (
                            <label key={version.id} className="flex cursor-pointer items-center gap-2 text-sm">
                                <Checkbox
                                    checked={selectedIds?.includes(version.id) ?? false}
                                    data-testid="analytics-version"
                                    data-version-id={version.id}
                                    onCheckedChange={checked => toggle(version.id, checked === true)}
                                />
                                <span className="inline-block size-2 rounded-full" style={{ background: seriesColor(version.number) }} />
                                Версия {version.number}
                                {version.isWorking && <Badge variant="secondary">рабочая</Badge>}
                                <span className="text-xs text-muted-foreground">{new Date(version.createdAt).toLocaleDateString(locale())}</span>
                            </label>
                        ))}
                    </div>
                )}
                <p className="text-xs text-muted-foreground">
                    Версии могут быть из разных запусков и обучены на разных датасетах: метрики таблицы посчитаны на validation своего датасета. Для честного
                    сравнения на одних кадрах используйте проверки ниже.
                </p>
            </section>
            {selected.length > 0 ? (
                <>
                    <section className="space-y-3 rounded-lg border bg-card p-5" data-testid="analytics-metrics">
                        <div className="flex flex-wrap items-center justify-between gap-3">
                            <h2 className="font-medium">Метрики и параметры</h2>
                            <Button variant="outline" size="sm" data-testid="analytics-copy" data-copied={copied || undefined} onClick={() => void copyTable()}>
                                {copied ? <Check /> : <Copy />}
                                {copied ? "Скопировано" : "Копировать таблицу"}
                            </Button>
                        </div>
                        {copyError && <p role="alert" className="text-sm text-destructive">{copyError}</p>}
                        <div className="overflow-x-auto rounded-md border">
                            <table className="w-full text-sm" data-testid="analytics-table">
                                <thead>
                                    <tr className="border-b text-left text-xs text-muted-foreground">
                                        <th className="px-3 py-2 font-normal">Параметр</th>
                                        {selected.map(version => (
                                            <th key={version.id} className="px-3 py-2 text-right font-normal" data-version-id={version.id}>
                                                <Button variant="link" size="sm" className="h-auto gap-1.5 p-0 text-foreground" title="Открыть паспорт версии"
                                                    data-testid="analytics-column" onClick={() => openPassport(version.id)}>
                                                    <span className="inline-block size-2 rounded-full" style={{ background: seriesColor(version.number) }} />
                                                    Версия {version.number}
                                                </Button>
                                            </th>
                                        ))}
                                    </tr>
                                </thead>
                                <tbody>
                                    {rows.map(row => {
                                        const values = selected.map(version => row.quality ? version.metrics[row.quality] : undefined);
                                        const best = row.quality ? bestOf(values) : null;
                                        return (
                                            <tr key={row.key} className="border-b last:border-b-0" data-row={row.key}>
                                                <td className="px-3 py-2 text-muted-foreground" title={row.help}>
                                                    <span className="inline-flex items-center gap-1">
                                                        {row.label}
                                                        {row.help && <CircleHelp className="size-3.5 opacity-60" aria-hidden />}
                                                    </span>
                                                </td>
                                                {selected.map((version, index) => {
                                                    const value = values[index];
                                                    const isBest = best !== null && value === best;
                                                    return (
                                                        <td key={version.id} data-version-id={version.id} data-best={isBest || undefined}
                                                            className={`px-3 py-2 text-right tabular-nums ${isBest ? "font-semibold text-[var(--metric-best)]" : ""}`}>
                                                            {row.text(version)}
                                                            {best !== null && typeof value === "number" && !isBest && (
                                                                <span className="ml-1.5 text-xs text-muted-foreground" title="Отставание от лучшей версии" data-testid="analytics-gap">
                                                                    −{(best - value).toFixed(3)}
                                                                </span>
                                                            )}
                                                        </td>
                                                    );
                                                })}
                                            </tr>
                                        );
                                    })}
                                </tbody>
                            </table>
                        </div>
                        <p className="text-xs text-muted-foreground">
                            Лучшее значение метрики в строке выделено зелёным, рядом с остальными — отставание от него. Наведите на название строки, чтобы увидеть пояснение;
                            заголовок версии открывает её паспорт.
                        </p>
                    </section>
                    <AnalyticsCurves versions={selected} histories={histories} />
                </>
            ) : (
                models.loaded && (
                    <div className="flex min-h-24 items-center justify-center rounded-md border border-dashed p-6 text-center text-sm text-muted-foreground" data-testid="analytics-none">
                        Отметьте одну или несколько версий, чтобы сравнить их.
                    </div>
                )
            )}
            {models.loaded && (
                <section className="space-y-3" data-testid="analytics-evaluations">
                    <h2 className="font-medium">Проверки на датасетах</h2>
                    <ModelEvaluations evaluations={evaluations} versions={versions} compared={selected} versionId={selected[0]?.id} datasets={datasets.versions} gpus={gpus} />
                </section>
            )}
        </div>
    );
}
