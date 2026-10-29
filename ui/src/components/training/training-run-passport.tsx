import { useEffect, useMemo, useRef, type ReactNode } from "react";
import { Loader2 } from "lucide-react";
import { useTranslation } from "react-i18next";
import type { ModelVersionResponseDto } from "@/api/projects/training/response-dto/model-version-response-dto";
import type { TrainingRunResponseDto } from "@/api/projects/training/response-dto/training-run-response-dto";
import { isActiveTrainingRun, trainingMetricLabels, trainingRunStateLabels, trainingStageLabels } from "@/api/projects/training/training-parameters";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { logLineText } from "@/lib/coded-text";
import { formatDate } from "@/lib/format";
import { lossSeries, qualitySeries } from "@/lib/training/epoch-chart";
import { count, environmentText, formatNumber, runError, runPercent } from "@/lib/training/run-view";
import { trainingLogWindow, useTrainingRunDetails } from "@/lib/use-training-run-details";
import { ModelVersionPassport } from "./model-version-passport";
import { TrainingEpochChart } from "./training-epoch-chart";
import { ParameterList } from "./training-parameter-list";

function Section({ title, testId, action, children }: { title: string; testId: string; action?: ReactNode; children: ReactNode }) {
    return (
        <section className="space-y-3 border-t pt-4" data-testid={testId} aria-label={title}>
            <div className="flex flex-wrap items-center justify-between gap-2">
                <h3 className="font-medium">{title}</h3>
                {action}
            </div>
            {children}
        </section>
    );
}

/**
 * "Run passport" in the tab "Current run": state, training progress ("Epoch X of Y", "Batch a of b", the bar, live metrics, the worker
 * stage), waiting and preparing, the error, environment, dataset and classes, "Epoch history" — the charts "Quality by epoch" and
 * "Losses by epoch" for the whole run, "Run parameters" (requested and actual), "Training log" (the last messages and the whole history
 * by pages), "Cancel training"; after "Done" — a short passport of the model version.
 */
export function TrainingRunPassport({ projectId, run, version, versionDeleted, busy, cancel, renewVersion, setWorking }: {
    projectId: string;
    run: TrainingRunResponseDto;
    version: ModelVersionResponseDto | undefined;
    versionDeleted: boolean;
    busy: boolean;
    cancel: () => void;
    renewVersion: () => void;
    setWorking: () => void;
}) {
    const { t, i18n } = useTranslation("training");
    const details = useTrainingRunDetails(projectId, run);
    // Series labels follow the interface language.
    const quality = useMemo(() => qualitySeries(details.epochs), [details.epochs, i18n.language]);
    const losses = useMemo(() => lossSeries(details.epochs), [details.epochs, i18n.language]);
    const logRef = useRef<HTMLDivElement>(null);
    const active = isActiveTrainingRun(run.state);
    const { progress } = run;
    const percent = runPercent(run);
    const error = runError(run);
    const environment = environmentText(run.environment);
    const metrics = Object.entries(progress.metrics).filter(([, value]) => Number.isFinite(value));

    // The last messages are visible at once: the log window scrolls to the end.
    useEffect(() => {
        if (details.logMode === "live" && logRef.current) logRef.current.scrollTop = logRef.current.scrollHeight;
    }, [details.log, details.logMode]);

    return (
        <article className="space-y-5" data-testid="training-run-passport" data-run-id={run.id} data-state={run.state} aria-label={t("runPassport.title")}>
            <header className="flex flex-wrap items-start justify-between gap-3">
                <div>
                    <h2 className="text-lg font-medium">{t("runPassport.title")}</h2>
                    <p className="mt-1 text-sm text-muted-foreground">
                        {run.presetName
                            ? t("runPassport.subtitlePreset", { dataset: run.dataset.name, date: formatDate(run.job.createdAt), preset: run.presetName })
                            : t("runPassport.subtitle", { dataset: run.dataset.name, date: formatDate(run.job.createdAt) })}
                    </p>
                </div>
                <div className="flex flex-wrap items-center gap-2">
                    <Badge variant={run.state === "failed" ? "destructive" : "secondary"} data-testid="training-run-state">{trainingRunStateLabels[run.state]}</Badge>
                    {active && (
                        <Button variant="outline" disabled={busy || run.state === "cancelling"} data-testid="training-cancel" onClick={cancel}>
                            {t("runPassport.cancel")}
                        </Button>
                    )}
                </div>
            </header>

            <section aria-label={t("runPassport.progress")} className="space-y-2" data-testid="training-progress">
                {progress.epoch !== null && progress.epochs ? (
                    <>
                        <div className="flex flex-wrap justify-between gap-2 text-sm tabular-nums">
                            <span data-testid="training-progress-epoch">{t("runPassport.epoch", { epoch: progress.epoch, total: progress.epochs })}</span>
                            {progress.batch !== null && progress.batches ? (
                                <span className="text-muted-foreground" data-testid="training-progress-batch">{t("runPassport.batch", { batch: progress.batch, total: progress.batches })}</span>
                            ) : null}
                        </div>
                    </>
                ) : (
                    <p className="text-sm text-muted-foreground" data-testid="training-progress-waiting">
                        {run.state === "queued"
                            ? t("runPassport.waitingGpu")
                            : active
                                ? t("runPassport.preparing")
                                : t("runPassport.noMetrics")}
                    </p>
                )}
                {(active || run.state === "succeeded") && (
                    <div className="h-2 overflow-hidden rounded-full bg-secondary" role="progressbar" aria-label={t("runPassport.progress")} aria-valuemin={0} aria-valuemax={100}
                        aria-valuenow={percent === null ? undefined : Math.round(percent)}>
                        <div className={percent === null ? "h-full w-1/3 animate-pulse bg-brand" : "h-full bg-brand transition-[width]"}
                            style={percent === null ? undefined : { width: `${percent}%` }} />
                    </div>
                )}
                {active && progress.stage && (
                    <p className="flex items-center gap-2 text-xs text-muted-foreground" data-testid="training-stage">
                        <Loader2 className="size-3.5 animate-spin" aria-hidden />{trainingStageLabels[progress.stage] ?? progress.stage}
                    </p>
                )}
                {metrics.length > 0 && (
                    <dl className="flex flex-wrap gap-x-5 gap-y-2 text-sm" data-testid="training-live-metrics">
                        {metrics.map(([key, value]) => (
                            <div key={key}>
                                <dt className="text-xs text-muted-foreground">{trainingMetricLabels[key] ?? key}</dt>
                                <dd className="tabular-nums">{formatNumber(value)}</dd>
                            </div>
                        ))}
                    </dl>
                )}
                {error && <p role="alert" className="text-sm text-destructive" data-testid="training-run-error">{error}</p>}
                <p className="text-xs text-muted-foreground">
                    {t("runPassport.times", { started: run.job.startedAt ? formatDate(run.job.startedAt) : "—", finished: run.job.finishedAt ? formatDate(run.job.finishedAt) : "—" })}
                </p>
                {environment && <p className="text-xs text-muted-foreground" data-testid="training-environment">{t("runPassport.environment", { environment })}</p>}
            </section>

            <Section title={t("runPassport.dataset")} testId="training-run-dataset">
                <p className="text-sm">
                    {t("runPassport.datasetCounts", { name: run.dataset.name, train: count(run.dataset.counts.train), val: count(run.dataset.counts.val), test: count(run.dataset.counts.test) })}
                </p>
                <p className="text-xs text-muted-foreground">
                    {t("runPassport.input", { architecture: run.architecture, width: run.input.width, height: run.input.height, imageSize: run.imageSize })}
                </p>
                <ol className="flex flex-wrap gap-x-5 gap-y-1 text-sm">
                    {run.dataset.classes.map(entry => <li key={entry.index}><span className="text-muted-foreground">{entry.index}.</span> {entry.name}</li>)}
                </ol>
            </Section>

            <Section title={t("runPassport.epochs")} testId="training-epochs">
                {details.epochs.length ? (
                    <>
                        <div className="grid gap-5 lg:grid-cols-2">
                            {quality.length > 0 && <TrainingEpochChart id="quality" label={t("runPassport.qualityChart")} series={quality} unit />}
                            {losses.length > 0 && <TrainingEpochChart id="losses" label={t("runPassport.lossChart")} series={losses} unit={false} />}
                        </div>
                        <p className="text-xs text-muted-foreground" data-testid="training-epochs-caption">
                            {t("runPassport.epochsCaption", { number: count(details.epochs.length) })}
                        </p>
                    </>
                ) : (
                    <p className="text-sm text-muted-foreground">
                        {!details.epochsLoaded ? t("runPassport.epochsLoading") : active ? t("runPassport.epochsWaiting") : t("runPassport.epochsMissing")}
                    </p>
                )}
            </Section>

            <details className="border-t pt-4" data-testid="training-run-parameters">
                <summary className="cursor-pointer font-medium">{t("runPassport.parameters")}</summary>
                <div className="mt-3 grid gap-5 sm:grid-cols-2">
                    <ParameterList title={t("passport.requested")} parameters={run.parameters} />
                    <ParameterList title={t("passport.actual")} parameters={run.effectiveParams} empty={t("runPassport.noActual")} />
                </div>
            </details>

            <Section
                title={t("runPassport.log")}
                testId="training-log"
                action={
                    <div className="flex gap-2">
                        <Button variant={details.logMode === "live" ? "secondary" : "outline"} size="sm" data-testid="training-log-live"
                            onClick={() => details.showLog("live")}>{t("runPassport.logLive")}</Button>
                        <Button variant={details.logMode === "history" ? "secondary" : "outline"} size="sm" data-testid="training-log-history"
                            onClick={() => details.showLog("history")}>{t("runPassport.logHistory")}</Button>
                    </div>
                }
            >
                <div ref={logRef} data-testid="training-log-lines" className="max-h-56 overflow-auto rounded-md border bg-muted/20 p-3 font-mono text-xs leading-relaxed" aria-live="off">
                    {details.log.length ? details.log.map(entry => (
                        <p key={entry.sequence} data-kind={entry.kind}
                            className={`whitespace-pre-wrap break-all ${entry.kind === "warning" ? "text-amber-700 dark:text-amber-400" : entry.kind === "stage" || entry.kind === "system" ? "text-muted-foreground" : ""}`}>
                            {logLineText(entry)}
                        </p>
                    )) : <p className="font-sans text-muted-foreground">{t("runPassport.logEmpty")}</p>}
                </div>
                <p className="text-xs text-muted-foreground" data-testid="training-log-caption">
                    {details.logMode === "live"
                        ? t("runPassport.logLiveCaption", { window: trainingLogWindow, total: count(details.logTotal) })
                        : t("runPassport.logHistoryCaption", { window: trainingLogWindow, total: count(details.logTotal) })}
                </p>
                {details.logMode === "history" && details.historyNext !== null && (
                    <Button variant="outline" size="sm" onClick={details.nextLogPage}>{t("runPassport.logNext")}</Button>
                )}
            </Section>
            {details.error && <p role="alert" className="text-sm text-destructive">{details.error}</p>}

            {run.resultVersionId && (
                <div className="border-t pt-4" id={`training-version-${run.resultVersionId}`}>
                    {version
                        ? <ModelVersionPassport version={version} epochs={details.epochs} busy={busy} renew={renewVersion} setWorking={setWorking} />
                        : <p className="text-sm text-muted-foreground" data-testid="training-version-missing">{versionDeleted ? t("runPassport.versionDeleted") : t("runPassport.versionLoading")}</p>}
                </div>
            )}
        </article>
    );
}
