import { Fragment, useEffect, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import type { EvaluationSplit } from "@/api/projects/checks/request-dto/checks-request-dto";
import type { ModelEvaluationResponseDto } from "@/api/projects/checks/response-dto/model-evaluation-response-dto";
import type { DatasetVersionResponseDto } from "@/api/projects/datasets/response-dto/dataset-response-dto";
import type { GpuInventoryResponseDto } from "@/api/projects/training/response-dto/training-runtime-response-dto";
import type { ModelVersionResponseDto } from "@/api/projects/training/response-dto/model-version-response-dto";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import "@/i18n/sections/checks";
import {
    bestOf, evaluationProblem, isEvaluationActive, preferredSplit, qualityMetricHelp, qualityMetricLabels, qualityMetrics, splitLabels, versionName,
} from "@/lib/checks/check-view";
import { errorMessage, formatDate, formatDay, formatMetric, formatNumber } from "@/lib/format";
import { seriesColor } from "@/lib/series-color";
import type { ModelEvaluationsState } from "@/lib/use-model-evaluations";
import { DeviceSelect, Field } from "./check-parameter-fields";
import { workerErrorText } from "@/lib/errors";

const pairKey = (datasetId: string, split: EvaluationSplit) => `${datasetId}|${split}`;

/**
 * Evaluation of versions on a dataset split: the form (version, a dataset that is neither archived nor corrupt, split, device, batch) with
 * compatibility hints, the comparison of the chosen versions on one dataset by the latest successful evaluation, and all checks with
 * cancel and per-class metrics. Shared by Checks and Analytics: the owner passes the versions of the matrix and the version of the form.
 */
export function ModelEvaluations({ evaluations, versions, compared, versionId, datasets, gpus }: {
    evaluations: ModelEvaluationsState;
    versions: readonly ModelVersionResponseDto[];
    /** Versions of the comparison matrix, in column order. */
    compared: readonly ModelVersionResponseDto[];
    /** The default version of the form; when it changes, the form follows it. */
    versionId: string | undefined;
    datasets: readonly DatasetVersionResponseDto[];
    gpus: GpuInventoryResponseDto | null | undefined;
}) {
    const { t } = useTranslation("checks");
    const usable = datasets.filter(item => !item.archivedAt && item.integrityState !== "corrupt");
    const [formVersionId, setFormVersionId] = useState(versionId);
    const [datasetId, setDatasetId] = useState<string>();
    const [split, setSplit] = useState<EvaluationSplit>();
    const [device, setDevice] = useState("");
    const [batch, setBatch] = useState("16");
    const [busy, setBusy] = useState(false);
    const [error, setError] = useState<string>();
    const [expanded, setExpanded] = useState<string>();
    const [compareKey, setCompareKey] = useState<string>();

    useEffect(() => setFormVersionId(versionId), [versionId]);

    const version = versions.find(item => item.id === formVersionId) ?? versions[0];
    const dataset = usable.find(item => item.id === datasetId) ?? usable[0];
    const part = split ?? preferredSplit(dataset);
    const problem = version && dataset ? evaluationProblem(version, dataset, part) : null;
    const batchValue = Number(batch);
    const validBatch = Number.isSafeInteger(batchValue) && batchValue >= 1 && batchValue <= 1024;
    const validDevice = /^(cpu|\d{1,3})$/.test(device);

    const datasetLabel = (id: string, fallback: string) => {
        const found = datasets.find(item => item.id === id);
        if (!found) return t("evaluation.datasetDeleted", { name: fallback });
        return found.archivedAt ? t("evaluation.datasetArchived", { name: found.name }) : found.name;
    };

    // Pairs "dataset · split" with successful evaluations; by default the pair of the form when it has evaluations.
    const pairs = [...new Map(evaluations.latest.map(item => [pairKey(item.dataset.versionId, item.split), item])).values()];
    const formPair = dataset ? pairKey(dataset.id, part) : undefined;
    const activeKey = compareKey && pairs.some(item => pairKey(item.dataset.versionId, item.split) === compareKey) ? compareKey
        : formPair && pairs.some(item => pairKey(item.dataset.versionId, item.split) === formPair) ? formPair
            : pairs[0] && pairKey(pairs[0].dataset.versionId, pairs[0].split);
    const activePair = pairs.find(item => pairKey(item.dataset.versionId, item.split) === activeKey);
    const matrix = compared.map(item => evaluations.latest.find(entry => entry.versionId === item.id && activePair
        && entry.dataset.versionId === activePair.dataset.versionId && entry.split === activePair.split));
    const pairDataset = activePair && datasets.find(item => item.id === activePair.dataset.versionId);

    async function launch(target: ModelVersionResponseDto, targetDataset: string, targetSplit: EvaluationSplit) {
        setBusy(true);
        setError(undefined);
        try {
            await evaluations.start({ versionId: target.id, datasetVersionId: targetDataset, split: targetSplit, device, batch: validBatch ? batchValue : null });
        } catch (cause) {
            setError(errorMessage(cause));
        } finally {
            setBusy(false);
        }
    }

    function submit(event: FormEvent) {
        event.preventDefault();
        if (version && dataset && !problem && validBatch && validDevice && !busy) void launch(version, dataset.id, part);
    }

    async function cancel(evaluationId: string) {
        setError(undefined);
        try {
            await evaluations.cancel(evaluationId);
        } catch (cause) {
            setError(errorMessage(cause));
        }
    }

    return (
        <section className="space-y-5 rounded-lg border bg-card p-5" data-testid="model-evaluations">
            <div>
                <h2 className="font-medium">{t("evaluation.title")}</h2>
                <p className="mt-1 max-w-prose text-sm text-muted-foreground">{t("evaluation.text")}</p>
            </div>
            <form className="space-y-3" onSubmit={submit}>
                <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-5">
                    <Field label={t("version.label")}>
                        <Select value={version?.id ?? ""} disabled={!versions.length} onValueChange={next => { if (next) setFormVersionId(next); }}>
                            <SelectTrigger aria-label={t("version.label")} className="w-full" data-testid="evaluation-version"><SelectValue placeholder={t("version.none")} /></SelectTrigger>
                            <SelectContent>
                                {versions.map(item => <SelectItem key={item.id} value={item.id}>{versionName(item)}</SelectItem>)}
                            </SelectContent>
                        </Select>
                    </Field>
                    <Field label={t("evaluation.dataset")}>
                        <Select value={dataset?.id ?? ""} disabled={!usable.length} onValueChange={next => { if (next) { setDatasetId(next); setSplit(undefined); } }}>
                            <SelectTrigger aria-label={t("evaluation.datasetAria")} className="w-full" data-testid="evaluation-dataset">
                                <SelectValue placeholder={t("evaluation.noDatasets")} />
                            </SelectTrigger>
                            <SelectContent>
                                {usable.map(item => <SelectItem key={item.id} value={item.id}>{item.name} · {formatDay(item.createdAt)}</SelectItem>)}
                            </SelectContent>
                        </Select>
                    </Field>
                    <Field label={t("evaluation.split")}>
                        <Select value={part} onValueChange={next => setSplit(next as EvaluationSplit)}>
                            <SelectTrigger aria-label={t("evaluation.split")} className="w-full" data-testid="evaluation-split"><SelectValue /></SelectTrigger>
                            <SelectContent>
                                <SelectItem value="val">Validation</SelectItem>
                                <SelectItem value="test">Test</SelectItem>
                            </SelectContent>
                        </Select>
                    </Field>
                    <Field label={t("fields.device")}>
                        <DeviceSelect value={device} onChange={setDevice} gpus={gpus} invalid={!!device && !validDevice} testId="evaluation-device" />
                    </Field>
                    <Field label={t("evaluation.batch")} htmlFor="evaluation-batch">
                        <Input id="evaluation-batch" type="number" min={1} max={1024} step={1} aria-invalid={!validBatch || undefined} data-testid="evaluation-batch"
                            value={batch} onChange={event => setBatch(event.target.value)} />
                    </Field>
                </div>
                <div className="min-h-4 text-xs" aria-live="polite" data-testid="evaluation-hint">
                    {!usable.length ? (
                        <p className="text-muted-foreground">{t("evaluation.noUsable")}</p>
                    ) : problem ? (
                        <p role="alert" className="text-destructive" data-testid="evaluation-problem">{problem}</p>
                    ) : !validBatch ? (
                        <p className="text-destructive">{t("evaluation.batchInvalid")}</p>
                    ) : dataset ? (
                        <p className="text-muted-foreground">
                            {t("evaluation.splitFrames", { count: dataset.counts[part], frames: formatNumber(dataset.counts[part]), split: splitLabels[part] })}
                        </p>
                    ) : null}
                </div>
                {error && <Alert variant="destructive" data-testid="evaluation-error"><AlertDescription>{error}</AlertDescription></Alert>}
                <Button type="submit" data-testid="evaluation-start" disabled={busy || !version || !dataset || !!problem || !validBatch || !validDevice}>
                    {t("evaluation.start")}
                </Button>
            </form>

            <div className="space-y-3 border-t pt-4" data-testid="evaluation-comparison">
                <div className="flex flex-wrap items-center justify-between gap-3">
                    <h3 className="text-sm font-medium">{t("evaluation.comparison")}</h3>
                    {pairs.length > 0 && (
                        <Select value={activeKey} onValueChange={setCompareKey}>
                            <SelectTrigger aria-label={t("evaluation.pairAria")} className="w-72" data-testid="evaluation-pair"><SelectValue /></SelectTrigger>
                            <SelectContent>
                                {pairs.map(item => (
                                    <SelectItem key={pairKey(item.dataset.versionId, item.split)} value={pairKey(item.dataset.versionId, item.split)}>
                                        {datasetLabel(item.dataset.versionId, item.dataset.name)} · {splitLabels[item.split]}
                                    </SelectItem>
                                ))}
                            </SelectContent>
                        </Select>
                    )}
                </div>
                {!pairs.length || !activePair ? (
                    <p className="text-sm text-muted-foreground">{t("evaluation.comparisonEmpty")}</p>
                ) : !compared.length ? (
                    <p className="text-sm text-muted-foreground">{t("evaluation.markVersions")}</p>
                ) : (
                    <div className="overflow-x-auto rounded-md border">
                        <table className="w-full text-sm" data-testid="evaluation-matrix">
                            <thead>
                                <tr className="border-b text-left text-xs text-muted-foreground">
                                    <th className="px-3 py-2 font-normal">{t("evaluation.metric")}</th>
                                    {compared.map((item, index) => (
                                        <th key={item.id} className="px-3 py-2 text-right font-normal" data-version-id={item.id}>
                                            <span className="inline-flex items-center gap-1.5">
                                                <span className="inline-block size-2 rounded-full" style={{ background: seriesColor(item.number) }} />
                                                {t("version.number", { number: item.number })}
                                            </span>
                                            {!matrix[index] && (
                                                <Button type="button" variant="link" size="sm" className="ml-1 h-auto p-0 text-xs" data-testid="evaluation-matrix-run"
                                                    disabled={busy || !validDevice || !pairDataset || !!evaluationProblem(item, pairDataset, activePair.split)}
                                                    onClick={() => void launch(item, activePair.dataset.versionId, activePair.split)}>
                                                    {t("evaluation.run")}
                                                </Button>
                                            )}
                                        </th>
                                    ))}
                                </tr>
                            </thead>
                            <tbody>
                                {qualityMetrics.map(key => {
                                    const values = matrix.map(item => item?.metrics[key] ?? null);
                                    const best = bestOf(values);
                                    return (
                                        <tr key={key} className="border-b last:border-b-0" data-metric={key}>
                                            <td className="px-3 py-2 text-muted-foreground" title={qualityMetricHelp[key]}>
                                                <span className="cursor-help underline decoration-dotted underline-offset-4">{qualityMetricLabels[key]}</span>
                                            </td>
                                            {values.map((value, index) => (
                                                <td key={compared[index].id} data-best={(best !== null && value === best) || undefined}
                                                    className={`px-3 py-2 text-right tabular-nums ${best !== null && value === best ? "font-semibold text-[var(--metric-best)]" : ""}`}>
                                                    {value === null
                                                        ? matrix[index] ? "—" : <span className="text-xs text-muted-foreground">{t("evaluation.notChecked")}</span>
                                                        : formatMetric(value)}
                                                </td>
                                            ))}
                                        </tr>
                                    );
                                })}
                            </tbody>
                        </table>
                    </div>
                )}
            </div>

            <div className="space-y-2 border-t pt-4">
                <h3 className="text-sm font-medium">{t("evaluation.all")}</h3>
                {evaluations.error && <p role="alert" className="text-sm text-destructive">{evaluations.error}</p>}
                {evaluations.evaluations.length ? (
                    <div className="overflow-x-auto rounded-md border">
                        <table className="w-full text-sm" data-testid="evaluation-list">
                            <thead>
                                <tr className="border-b text-left text-xs text-muted-foreground">
                                    <th className="px-3 py-2 font-normal">{t("evaluation.columnVersion")}</th>
                                    <th className="px-3 py-2 font-normal">{t("evaluation.columnDataset")}</th>
                                    <th className="px-3 py-2 font-normal">{t("evaluation.columnState")}</th>
                                    {qualityMetrics.map(key => <th key={key} className="px-3 py-2 text-right font-normal" title={qualityMetricHelp[key]}>{qualityMetricLabels[key]}</th>)}
                                    <th className="px-3 py-2 font-normal">{t("evaluation.columnDate")}</th>
                                    <th className="px-3 py-2" />
                                </tr>
                            </thead>
                            <tbody>
                                {evaluations.evaluations.map(item => (
                                    <EvaluationRow key={item.id} item={item} datasetLabel={datasetLabel} expanded={expanded === item.id}
                                        toggle={() => setExpanded(current => current === item.id ? undefined : item.id)} cancel={() => void cancel(item.id)} />
                                ))}
                            </tbody>
                        </table>
                    </div>
                ) : (
                    <p className="text-sm text-muted-foreground" data-testid="evaluation-list-empty">
                        {evaluations.loading ? t("loading") : t("evaluation.listEmpty")}
                    </p>
                )}
                {evaluations.total > evaluations.evaluations.length && (
                    <p className="text-xs text-muted-foreground">
                        {t("evaluation.shownLatest", { shown: formatNumber(evaluations.evaluations.length), total: formatNumber(evaluations.total) })}
                    </p>
                )}
            </div>
        </section>
    );
}

function EvaluationRow({ item, datasetLabel, expanded, toggle, cancel }: {
    item: ModelEvaluationResponseDto;
    datasetLabel: (id: string, fallback: string) => string;
    expanded: boolean;
    toggle: () => void;
    cancel: () => void;
}) {
    const { t } = useTranslation("checks");
    const active = isEvaluationActive(item.state);
    const columns = 9;
    return (
        <Fragment>
            <tr className="border-b last:border-b-0" data-testid="evaluation-row" data-evaluation-id={item.id} data-state={item.state}>
                <td className="px-3 py-2">
                    <span className="inline-flex items-center gap-1.5">
                        <span className="inline-block size-2 rounded-full" style={{ background: seriesColor(item.versionNumber) }} />
                        {t("version.number", { number: item.versionNumber })}
                    </span>
                </td>
                <td className="px-3 py-2">
                    {datasetLabel(item.dataset.versionId, item.dataset.name)} · {splitLabels[item.split]}
                    {item.imageCount !== null && (
                        <span className="text-xs text-muted-foreground"> · {t("evaluation.frames", { count: item.imageCount, frames: formatNumber(item.imageCount) })}</span>
                    )}
                </td>
                <td className={`px-3 py-2 ${item.state === "failed" ? "text-destructive" : ""}`} data-testid="evaluation-row-state">{t(`evaluationState.${item.state}`)}</td>
                {qualityMetrics.map(key => <td key={key} className="px-3 py-2 text-right tabular-nums">{formatMetric(item.metrics[key])}</td>)}
                <td className="px-3 py-2 text-xs text-muted-foreground">{formatDate(item.job.createdAt)}</td>
                <td className="px-3 py-2 text-right">
                    {active ? (
                        <Button variant="ghost" size="sm" disabled={item.state === "cancelling"} data-testid="evaluation-cancel" onClick={cancel}>{t("evaluation.cancel")}</Button>
                    ) : item.classMetrics.length ? (
                        <Button variant="ghost" size="sm" data-testid="evaluation-classes-toggle" aria-expanded={expanded} onClick={toggle}>
                            {expanded ? t("evaluation.hideClasses") : t("evaluation.byClass")}
                        </Button>
                    ) : null}
                </td>
            </tr>
            {item.error && (item.state === "failed" || item.state === "interrupted") && (
                <tr className="border-b last:border-b-0">
                    <td colSpan={columns} className="px-3 pb-2 text-xs text-destructive" data-testid="evaluation-row-error">{workerErrorText(item.error)}</td>
                </tr>
            )}
            {expanded && (
                <tr className="border-b bg-background/40 last:border-b-0">
                    <td colSpan={columns} className="px-3 py-2">
                        <table className="w-full text-xs" data-testid="evaluation-classes">
                            <thead>
                                <tr className="text-left text-muted-foreground">
                                    <th className="py-1 font-normal">{t("results.class")}</th>
                                    {qualityMetrics.map(key => <th key={key} className="py-1 text-right font-normal">{qualityMetricLabels[key]}</th>)}
                                </tr>
                            </thead>
                            <tbody>
                                {item.classMetrics.map(entry => (
                                    <tr key={entry.index}>
                                        <td className="py-1">{entry.name}</td>
                                        {qualityMetrics.map(key => <td key={key} className="py-1 text-right tabular-nums">{formatMetric(entry.metrics[key])}</td>)}
                                    </tr>
                                ))}
                            </tbody>
                        </table>
                        <p className="mt-1 text-muted-foreground">{t("evaluation.classesNote")}</p>
                    </td>
                </tr>
            )}
        </Fragment>
    );
}
