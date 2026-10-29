import { useTranslation } from "react-i18next";
import type { ModelVersionResponseDto } from "@/api/projects/training/response-dto/model-version-response-dto";
import type { TrainingRunResponseDto } from "@/api/projects/training/response-dto/training-run-response-dto";
import { isActiveTrainingRun, trainingRunStateLabels } from "@/api/projects/training/training-parameters";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { formatDate, formatMetric, locale } from "@/lib/format";
import { deviceLabel, formatNumber, parameterText, runError, runPercent } from "@/lib/training/run-view";

/**
 * A card of "Training history": state, dataset, parameters, progress, error, "Run passport" (the tab "Current run"), "Version N
 * passport" — the short version passport under the run passport (a deleted version — "Version deleted"), "Cancel" for a running run.
 */
export function TrainingRunCard({ run, version, versionDeleted, busy, openRun, openVersion, cancel }: {
    run: TrainingRunResponseDto;
    version: ModelVersionResponseDto | undefined;
    /** The version of the run was deleted: "Version deleted" instead of "Version passport". */
    versionDeleted: boolean;
    busy: boolean;
    openRun: () => void;
    openVersion: () => void;
    cancel: () => void;
}) {
    const { t } = useTranslation("training");
    const active = isActiveTrainingRun(run.state);
    const parameters = run.parameters;
    const summary = [
        formatDate(run.job.createdAt),
        t("card.epochs", { value: parameterText(parameters.epochs) }),
        `batch ${parameterText(parameters.batch)}`,
        deviceLabel(parameters.device),
        `seed ${parameterText(parameters.seed)}`,
        `AMP ${parameterText(parameters.amp).toLocaleLowerCase(locale())}`,
        parameterText(parameters.optimizer),
        ...(typeof parameters.lr0 === "number" ? [`lr0 ${formatNumber(parameters.lr0, 6)}`] : []),
        ...(run.presetName ? [t("card.preset", { name: run.presetName })] : []),
    ];
    const error = runError(run);
    const percent = runPercent(run);
    const { progress } = run;
    return (
        <li className="space-y-3 rounded-lg border bg-card p-4" data-testid="training-run-card" data-run-id={run.id} data-state={run.state}>
            <div className="flex flex-wrap items-start justify-between gap-3">
                <div className="min-w-0">
                    <p className="flex flex-wrap items-center gap-2 text-sm">
                        <span className="font-medium">{run.dataset.name}</span>
                        <Badge variant={run.state === "failed" ? "destructive" : "secondary"} data-testid="training-run-card-state">{trainingRunStateLabels[run.state]}</Badge>
                        {version && <span className="text-muted-foreground">{t(version.isWorking ? "card.versionWorking" : "card.version", { number: version.number })}</span>}
                    </p>
                    <p className="mt-1 text-xs text-muted-foreground" data-testid="training-run-card-parameters">
                        {summary.join(" · ")}
                    </p>
                </div>
                <div className="flex flex-wrap gap-2">
                    <Button variant="outline" size="sm" data-testid="training-run-card-open" onClick={openRun}>{t("runPassport.title")}</Button>
                    {run.resultVersionId && versionDeleted && (
                        <span className="self-center text-xs text-muted-foreground" data-testid="training-run-card-version-deleted">{t("card.versionDeleted")}</span>
                    )}
                    {run.resultVersionId && !versionDeleted && (
                        <Button size="sm" data-testid="training-run-card-version" onClick={openVersion}>
                            {version ? t("card.versionPassport", { number: version.number }) : t("card.modelPassport")}
                        </Button>
                    )}
                    {active && (
                        <Button variant="ghost" size="sm" disabled={busy || run.state === "cancelling"} data-testid="training-run-card-cancel" onClick={cancel}>
                            {t("card.cancel")}
                        </Button>
                    )}
                </div>
            </div>
            {progress.epoch !== null && progress.epochs ? (
                <div className="flex flex-wrap items-center gap-3 text-xs">
                    <span className="tabular-nums" data-testid="training-run-card-epoch">{t("runPassport.epoch", { epoch: progress.epoch, total: progress.epochs })}</span>
                    {active && (
                        <div className="h-1.5 w-48 overflow-hidden rounded-full bg-secondary" role="progressbar" aria-label={t("runPassport.progress")} aria-valuemin={0} aria-valuemax={100}
                            aria-valuenow={percent === null ? undefined : Math.round(percent)}>
                            <div className="h-full bg-brand transition-[width]" style={{ width: `${percent ?? 0}%` }} />
                        </div>
                    )}
                    {active && progress.batch !== null && progress.batches ? <span className="tabular-nums text-muted-foreground">{t("card.batch", { batch: progress.batch, total: progress.batches })}</span> : null}
                    {version && <span className="text-muted-foreground">mAP50 {formatMetric(version.metrics.mAP50)} · mAP50-95 {formatMetric(version.metrics.mAP50_95)}</span>}
                </div>
            ) : active && (
                <p className="text-xs text-muted-foreground">
                    {run.state === "queued" ? t("card.queued") : t("card.preparing")}
                </p>
            )}
            {error && <p role="alert" className="text-xs text-destructive" data-testid="training-run-card-error">{error}</p>}
        </li>
    );
}
