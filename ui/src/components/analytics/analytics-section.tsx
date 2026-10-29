import { useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { Check, CircleHelp, Copy } from "lucide-react";
import type { ProjectResponseDto } from "@/api/projects/response-dto/project-response-dto";
import { ModelEvaluations } from "@/components/checks/model-evaluations";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { analyticsRows, analyticsTsv, defaultSelectionSize, readSelection, writeSelection } from "@/lib/analytics/analytics-view";
import { bestOf } from "@/lib/checks/check-view";
import { readEpochHistory } from "@/lib/training/epoch-history";
import { useDatasetVersions } from "@/lib/use-dataset-versions";
import { useGpuInventory } from "@/lib/use-gpu-inventory";
import { useModelEvaluations } from "@/lib/use-model-evaluations";
import { useModelVersions } from "@/lib/use-model-versions";
import { AnalyticsCurves, type EpochHistory } from "./analytics-curves";
import "@/i18n/sections/analytics";
import { formatDay } from "@/lib/format";
import { seriesColor } from "@/lib/series-color";

/**
 * The Analytics section: comparison of the chosen model versions — metrics and parameters, training curves by epoch and dataset checks
 * (the shared ModelEvaluations). No polling: training.changed, datasets.changed, checks.changed, job.changed. The choice of versions is
 * kept per project in localStorage; the epoch history is read once per version while the section is open.
 */
export function AnalyticsSection({ project, openTraining, openPassport }: {
    project: ProjectResponseDto;
    openTraining: () => void;
    openPassport: (versionId: string) => void;
}) {
    const { t, i18n } = useTranslation("analytics");
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
    // Rows hold texts of the current language: a language change builds them again.
    const rows = useMemo(() => analyticsRows(datasets.versions), [datasets.versions, i18n.language]);

    useEffect(() => {
        mounted.current = true;
        return () => { mounted.current = false; };
    }, []);

    useEffect(() => {
        // The first choice is the saved one or the latest three versions; deleted versions leave the choice.
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
            setCopyError(t("metrics.copyFailed"));
            return;
        }
        if (!mounted.current) return;
        setCopied(true);
        window.setTimeout(() => { if (mounted.current) setCopied(false); }, 2000);
    }

    if (models.loaded && !versions.length) {
        return (
            <section className="space-y-3 rounded-lg border border-dashed p-6" data-testid="analytics-empty">
                <h2 className="font-medium">{t("empty.title")}</h2>
                <p className="max-w-prose text-sm text-muted-foreground">{t("empty.text")}</p>
                <Button variant="outline" data-testid="analytics-open-training" onClick={openTraining}>{t("empty.openTraining")}</Button>
            </section>
        );
    }

    return (
        <div className="space-y-5" data-testid="analytics-section">
            {models.error && (
                <Alert variant="destructive">
                    <AlertDescription>
                        <p>{models.error}</p>
                        <Button variant="ghost" size="sm" className="mt-1" onClick={() => void models.refresh()}>{t("retry")}</Button>
                    </AlertDescription>
                </Alert>
            )}
            <section className="space-y-3 rounded-lg border bg-card p-5" data-testid="analytics-versions">
                <h2 className="font-medium">{t("versions.title")}</h2>
                {!models.loaded ? (
                    <p role="status" className="text-sm text-muted-foreground">{t("versions.loading")}</p>
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
                                {t("table.version", { number: version.number })}
                                {version.isWorking && <Badge variant="secondary">{t("versions.working")}</Badge>}
                                <span className="text-xs text-muted-foreground">{formatDay(version.createdAt)}</span>
                            </label>
                        ))}
                    </div>
                )}
                <p className="text-xs text-muted-foreground">{t("versions.note")}</p>
            </section>
            {selected.length > 0 ? (
                <>
                    <section className="space-y-3 rounded-lg border bg-card p-5" data-testid="analytics-metrics">
                        <div className="flex flex-wrap items-center justify-between gap-3">
                            <h2 className="font-medium">{t("metrics.title")}</h2>
                            <Button variant="outline" size="sm" data-testid="analytics-copy" data-copied={copied || undefined} onClick={() => void copyTable()}>
                                {copied ? <Check /> : <Copy />}
                                {copied ? t("metrics.copied") : t("metrics.copy")}
                            </Button>
                        </div>
                        {copyError && <p role="alert" className="text-sm text-destructive">{copyError}</p>}
                        <div className="overflow-x-auto rounded-md border">
                            <table className="w-full text-sm" data-testid="analytics-table">
                                <thead>
                                    <tr className="border-b text-left text-xs text-muted-foreground">
                                        <th className="px-3 py-2 font-normal">{t("table.parameter")}</th>
                                        {selected.map(version => (
                                            <th key={version.id} className="px-3 py-2 text-right font-normal" data-version-id={version.id}>
                                                <Button variant="link" size="sm" className="h-auto gap-1.5 p-0 text-foreground" title={t("metrics.openPassport")}
                                                    data-testid="analytics-column" onClick={() => openPassport(version.id)}>
                                                    <span className="inline-block size-2 rounded-full" style={{ background: seriesColor(version.number) }} />
                                                    {t("table.version", { number: version.number })}
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
                                                                <span className="ml-1.5 text-xs text-muted-foreground" title={t("metrics.gap")} data-testid="analytics-gap">
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
                        <p className="text-xs text-muted-foreground">{t("metrics.note")}</p>
                    </section>
                    <AnalyticsCurves versions={selected} histories={histories} />
                </>
            ) : (
                models.loaded && (
                    <div className="flex min-h-24 items-center justify-center rounded-md border border-dashed p-6 text-center text-sm text-muted-foreground" data-testid="analytics-none">
                        {t("none")}
                    </div>
                )
            )}
            {models.loaded && (
                <section className="space-y-3" data-testid="analytics-evaluations">
                    <h2 className="font-medium">{t("evaluations")}</h2>
                    <ModelEvaluations evaluations={evaluations} versions={versions} compared={selected} versionId={selected[0]?.id} datasets={datasets.versions} gpus={gpus} />
                </section>
            )}
        </div>
    );
}
