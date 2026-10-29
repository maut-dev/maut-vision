import { useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import type { ModelVersionResponseDto } from "@/api/projects/training/response-dto/model-version-response-dto";
import type { TrainingEpochDto } from "@/api/projects/training/response-dto/training-run-response-dto";
import { isResourceUrl, type ResourceUrl } from "@/api/resources/resource-url";
import { ResourceImage } from "@/components/resource-image";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { ImageViewer } from "@/components/viewer/image-viewer";
import { workerWarningText } from "@/lib/coded-text";
import { formatDate, formatMetric, locale } from "@/lib/format";
import { i18n } from "@/i18n";
import {
    deviceLabel,
    fileKindLabels,
    formatBytes,
    formatNumber,
    parameterText,
    plotCaption,
    sortedPlots,
} from "@/lib/training/run-view";
import { ParameterList } from "./training-parameter-list";

const metricStats = ["mAP50", "mAP50_95", "precision", "recall"] as const;

function maxOf(epochs: readonly TrainingEpochDto[], key: string): number | null {
    const values = epochs.map(entry => entry.metrics[key]).filter((value): value is number => typeof value === "number" && Number.isFinite(value));
    return values.length ? Math.max(...values) : null;
}

function Row({ label, children }: { label: string; children: ReactNode }) {
    return (
        <>
            <dt className="text-muted-foreground">{label}</dt>
            <dd className="min-w-0 break-words tabular-nums">{children}</dd>
        </>
    );
}

function Stat({ label, value, hint, testId }: { label: string; value: ReactNode; hint?: string; testId: string }) {
    return (
        <div className="rounded-lg border bg-card p-4" data-testid={testId}>
            <p className="text-xs text-muted-foreground">{label}</p>
            <p className="mt-1 text-2xl font-semibold tabular-nums">{value}</p>
            <p className="mt-1 min-h-4 text-xs text-muted-foreground">{hint}</p>
        </div>
    );
}

/** "0.0005 (tuned)" — by the source of the actual value of the worker. */
function sourced(effective: Record<string, unknown>, key: string): string {
    const value = effective[key];
    const text = typeof value === "number" ? formatNumber(value, 6) : parameterText(value);
    const source = effective[`${key}Source`];
    if (source === "computed") return i18n.t("training:passport.tuned", { value: text });
    if (source === "optimizer-auto") return i18n.t("training:passport.chosenByOptimizer", { value: text });
    return text;
}

/**
 * The passport of a model version (under a run in "Training" and in "Models" with export, deletion and a way back to the list): metrics
 * of best.pt with the best values by epoch, the best epoch and early stopping, parameters (requested and actual), classes in the order of
 * outputs, worker notes, files with SHA-256 and training images — they open in the shared viewer with moves between them.
 */
export function ModelVersionPassport({ version, epochs, busy, renew, setWorking, exportZip, remove, back }: {
    version: ModelVersionResponseDto;
    epochs: readonly TrainingEpochDto[];
    busy: boolean;
    renew: () => void;
    setWorking: () => void;
    /** "Export ZIP" and "Delete" — the passport in "Models". */
    exportZip?: () => void;
    remove?: () => void;
    /** "← Back to models". */
    back?: () => void;
}) {
    const { t } = useTranslation("training");
    const [opened, setOpened] = useState<{ index: number; width: number; height: number }>();
    const parameters = version.parameters;
    const effective = version.effectiveParams;
    const plots = sortedPlots(version.files);
    const others = version.files.filter(file => file.kind !== "plot");
    const requestedEpochs = typeof parameters.epochs === "number" ? parameters.epochs : version.epochsCompleted;
    const effectiveBatch = effective.effectiveBatch;
    const openedPlot = opened ? plots[opened.index] : undefined;

    async function open(index: number) {
        const url = plots[index]?.url;
        let size = { width: 1280, height: 960 };
        if (url) {
            try {
                const image = new Image();
                image.src = url;
                await image.decode();
                size = { width: image.naturalWidth || size.width, height: image.naturalHeight || size.height };
            } catch {
                // The viewer refines the size; it shows a loading error itself.
            }
        }
        setOpened({ index, ...size });
    }

    return (
        <section className="space-y-4" data-testid="model-version-passport" data-version-id={version.id} aria-label={t("passport.version", { number: version.number })}>
            {back && (
                <Button variant="link" size="sm" className="h-auto px-0" data-testid="model-version-back" onClick={back}>{t("passport.back")}</Button>
            )}
            <div className="flex flex-wrap items-start justify-between gap-3">
                <div>
                    <h3 className="flex items-center gap-2 text-lg font-medium">
                        {t("passport.version", { number: version.number })}
                        {version.isWorking && <Badge data-testid="model-version-working">{t("passport.workingBadge")}</Badge>}
                    </h3>
                    <p className="mt-1 text-xs text-muted-foreground">
                        {t("passport.subtitle", { architecture: version.architecture, width: version.input.width, height: version.input.height, date: formatDate(version.createdAt) })}
                    </p>
                </div>
                <div className="flex flex-wrap gap-2">
                    {!version.isWorking && <Button variant="outline" size="sm" disabled={busy} data-testid="model-version-set-working" onClick={setWorking}>{t("passport.makeWorking")}</Button>}
                    {exportZip && <Button variant="outline" size="sm" disabled={busy} data-testid="model-version-export" onClick={exportZip}>{t("passport.exportZip")}</Button>}
                    {remove && <Button variant="ghost" size="sm" disabled={busy} data-testid="model-version-delete" onClick={remove}>{t("passport.delete")}</Button>}
                </div>
            </div>

            <div className="grid grid-cols-2 gap-3 md:grid-cols-5" data-testid="model-version-metrics">
                {metricStats.map(key => {
                    const top = maxOf(epochs, key);
                    return <Stat key={key} testId={`model-version-metric-${key}`} label={t(`passport.metrics.${key}`)} value={formatMetric(version.metrics[key])}
                        hint={top !== null ? t("passport.best", { value: formatMetric(top) }) : t("passport.bestPt")} />;
                })}
                <Stat testId="model-version-best-epoch" label={t("passport.bestEpoch")} value={version.bestEpoch ?? "—"}
                    hint={t(version.stoppedEarly ? "passport.epochsDoneStopped" : "passport.epochsDone", { done: version.epochsCompleted, total: requestedEpochs })} />
            </div>

            <div className="grid gap-4 lg:grid-cols-2">
                <div className="space-y-4 rounded-lg border bg-card p-5">
                    <h4 className="font-medium">{t("passport.parameters")}</h4>
                    <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 text-sm" data-testid="model-version-parameters">
                        <Row label={t("passport.rows.status")}>{version.isWorking ? t("passport.statusWorking") : t("passport.statusTrained")}</Row>
                        <Row label={t("passport.rows.dataset")}>{version.dataset.name}</Row>
                        <Row label={t("passport.rows.architecture")}>{version.architecture}</Row>
                        <Row label={t("passport.rows.epochs")}>
                            {t(version.stoppedEarly ? "passport.epochsOfStopped" : "passport.epochsOf", { done: version.epochsCompleted, total: requestedEpochs })}
                        </Row>
                        <Row label={t("passport.rows.imageSize")}>{version.imageSize}</Row>
                        <Row label="batch">
                            {typeof effectiveBatch === "number" && effectiveBatch !== parameters.batch
                                ? t("passport.actualBatch", { value: parameterText(parameters.batch), actual: effectiveBatch })
                                : parameterText(parameters.batch)}
                        </Row>
                        <Row label={t("passport.rows.optimizer")}>{parameterText(effective.optimizer ?? parameters.optimizer)}</Row>
                        <Row label={t("passport.rows.lr0")}>{sourced(effective, "lr0")}</Row>
                        <Row label={t("passport.rows.warmup")}>{sourced(effective, "warmupEpochs")}</Row>
                        <Row label={t("passport.rows.classes")}>{version.classes.length}</Row>
                        <Row label={t("passport.rows.device")}>
                            {t("passport.deviceAmp", { device: deviceLabel(parameters.device), amp: parameterText(effective.amp ?? parameters.amp).toLocaleLowerCase(locale()) })}
                        </Row>
                        <Row label={t("passport.rows.created")}>{formatDate(version.createdAt)}</Row>
                    </dl>
                    <div className="space-y-1 text-xs">
                        <p className="text-muted-foreground">{t("passport.classesOrder")}</p>
                        <div className="max-h-64 overflow-auto rounded-md border" tabIndex={0} aria-label={t("passport.outputMap")}>
                            <table className="w-full text-left text-xs">
                                <thead className="sticky top-0 bg-card text-muted-foreground">
                                    <tr><th className="px-3 py-2">{t("passport.index")}</th><th className="px-3 py-2">{t("passport.class")}</th><th className="px-3 py-2">{t("passport.stableId")}</th></tr>
                                </thead>
                                <tbody>
                                    {[...version.classes].sort((left, right) => left.index - right.index).map(entry => (
                                        <tr key={entry.index} className="border-t">
                                            <td className="px-3 py-2 tabular-nums">{entry.index}</td>
                                            <td className="px-3 py-2">{entry.name}</td>
                                            <td className="break-all px-3 py-2 text-muted-foreground">{entry.classId ?? t("passport.merged", { number: entry.sourceClassIds.length })}</td>
                                        </tr>
                                    ))}
                                </tbody>
                            </table>
                        </div>
                    </div>
                    <details className="border-t pt-3">
                        <summary className="cursor-pointer text-sm font-medium">{t("passport.allParameters")}</summary>
                        <div className="mt-3 space-y-4">
                            <ParameterList title={t("passport.requested")} parameters={parameters} />
                            <ParameterList title={t("passport.actual")} parameters={effective} empty={t("passport.noActual")} />
                        </div>
                    </details>
                    {version.notes.length > 0 && (
                        <div className="space-y-1 text-xs" data-testid="model-version-notes">
                            <p className="text-muted-foreground">{t("passport.notes")}</p>
                            {version.notes.map((note, index) => <p key={index} className="whitespace-pre-wrap">{workerWarningText(note)}</p>)}
                        </div>
                    )}
                </div>
                <div className="space-y-4 rounded-lg border bg-card p-5">
                    <h4 className="font-medium">{t("passport.files")}</h4>
                    <ul className="space-y-2 text-sm" data-testid="model-version-files">
                        {others.map(file => (
                            <li key={file.name} data-file-kind={file.kind}>
                                <span className="text-muted-foreground">{fileKindLabels[file.kind]}:</span> {file.name} · {formatBytes(file.byteSize)}
                                <span className="block truncate font-mono text-[11px] text-muted-foreground" title={file.sha256}>SHA-256 {file.sha256}</span>
                            </li>
                        ))}
                        <li className="text-xs text-muted-foreground">{t("passport.imagesCount", { number: plots.length })}</li>
                    </ul>
                    {environmentRows(version.environment)}
                </div>
            </div>

            <div className="space-y-3 rounded-lg border bg-card p-5">
                <div>
                    <h4 className="font-medium">{t("passport.images")}</h4>
                    <p className="mt-1 text-xs text-muted-foreground">
                        {plots.length ? t("passport.imagesHint") : t("passport.noImages")}
                    </p>
                </div>
                {plots.length > 0 && (
                    <div className="grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-4" data-testid="model-version-plots">
                        {plots.map((plot, index) => (
                            <button key={plot.name} type="button" className="group space-y-1 text-left" data-testid="model-version-plot" data-plot-name={plot.name}
                                onClick={() => void open(index)}>
                                <ResourceImage src={plot.url && isResourceUrl(plot.url) ? plot.url : null} alt={plotCaption(plot.name)} fit="contain"
                                    className="aspect-[4/3] w-full transition-opacity group-hover:opacity-90" onExpired={renew} />
                                <p className="truncate text-xs text-muted-foreground">{plotCaption(plot.name)} <span className="font-mono">· {plot.name}</span></p>
                            </button>
                        ))}
                    </div>
                )}
            </div>

            {opened && openedPlot && (
                <ImageViewer
                    source={{
                        id: `${version.id}:${openedPlot.name}`,
                        name: `${plotCaption(openedPlot.name)} · ${openedPlot.name}`,
                        width: opened.width,
                        height: opened.height,
                        src: openedPlot.url && isResourceUrl(openedPlot.url) ? openedPlot.url as ResourceUrl : null,
                        renew,
                        metadata: <span>{formatBytes(openedPlot.byteSize)} · SHA-256 {openedPlot.sha256}</span>,
                    }}
                    close={() => setOpened(undefined)}
                    position={`${opened.index + 1} / ${plots.length}`}
                    onPrevious={opened.index > 0 ? () => void open(opened.index - 1) : undefined}
                    onNext={opened.index < plots.length - 1 ? () => void open(opened.index + 1) : undefined}
                />
            )}
        </section>
    );
}

function environmentRows(environment: Record<string, unknown>) {
    const rows = (["deviceName", "python", "torch", "ultralytics", "cuda"] as const)
        .filter(key => typeof environment[key] === "string" && environment[key]);
    if (!rows.length) return null;
    const labels: Record<(typeof rows)[number], string> = {
        deviceName: i18n.t("training:passport.rows.device"), python: "Python", torch: "torch", ultralytics: "ultralytics", cuda: "CUDA",
    };
    return (
        <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 border-t pt-3 text-xs" data-testid="model-version-environment">
            {rows.map(key => <Row key={key} label={labels[key]}>{String(environment[key])}</Row>)}
        </dl>
    );
}
