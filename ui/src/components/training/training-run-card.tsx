import type { ModelVersionResponseDto } from "@/api/projects/training/response-dto/model-version-response-dto";
import type { TrainingRunResponseDto } from "@/api/projects/training/response-dto/training-run-response-dto";
import { isActiveTrainingRun, trainingRunStateLabels } from "@/api/projects/training/training-parameters";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { formatDate, locale } from "@/lib/format";
import { deviceLabel, formatMetric, formatNumber, parameterText, runError, runPercent } from "@/lib/training/run-view";

/**
 * Карточка «История обучения»: состояние, датасет, параметры, ход, ошибка, «Паспорт запуска» (вкладка «Текущий запуск»),
 * «Паспорт версии N» — краткий паспорт версии под паспортом запуска (удалённая версия — «Версия удалена»), «Отменить» у идущего запуска.
 */
export function TrainingRunCard({ run, version, versionDeleted, busy, openRun, openVersion, cancel }: {
    run: TrainingRunResponseDto;
    version: ModelVersionResponseDto | undefined;
    /** Версия запуска удалена: вместо «Паспорт версии» — «Версия удалена». */
    versionDeleted: boolean;
    busy: boolean;
    openRun: () => void;
    openVersion: () => void;
    cancel: () => void;
}) {
    const active = isActiveTrainingRun(run.state);
    const parameters = run.parameters;
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
                        {version && <span className="text-muted-foreground">версия {version.number}{version.isWorking && " · рабочая"}</span>}
                    </p>
                    <p className="mt-1 text-xs text-muted-foreground" data-testid="training-run-card-parameters">
                        {formatDate(run.job.createdAt)} · {parameterText(parameters.epochs)} эпох · batch {parameterText(parameters.batch)} · {deviceLabel(parameters.device)}
                        {" "}· seed {parameterText(parameters.seed)} · AMP {parameterText(parameters.amp).toLocaleLowerCase(locale())} · {parameterText(parameters.optimizer)}
                        {typeof parameters.lr0 === "number" && ` · lr0 ${formatNumber(parameters.lr0, 6)}`}
                        {run.presetName && ` · пресет «${run.presetName}»`}
                    </p>
                </div>
                <div className="flex flex-wrap gap-2">
                    <Button variant="outline" size="sm" data-testid="training-run-card-open" onClick={openRun}>Паспорт запуска</Button>
                    {run.resultVersionId && versionDeleted && (
                        <span className="self-center text-xs text-muted-foreground" data-testid="training-run-card-version-deleted">Версия удалена</span>
                    )}
                    {run.resultVersionId && !versionDeleted && (
                        <Button size="sm" data-testid="training-run-card-version" onClick={openVersion}>
                            Паспорт версии {version?.number ?? "модели"}
                        </Button>
                    )}
                    {active && (
                        <Button variant="ghost" size="sm" disabled={busy || run.state === "cancelling"} data-testid="training-run-card-cancel" onClick={cancel}>
                            Отменить
                        </Button>
                    )}
                </div>
            </div>
            {progress.epoch !== null && progress.epochs ? (
                <div className="flex flex-wrap items-center gap-3 text-xs">
                    <span className="tabular-nums" data-testid="training-run-card-epoch">Эпоха {progress.epoch} из {progress.epochs}</span>
                    {active && (
                        <div className="h-1.5 w-48 overflow-hidden rounded-full bg-secondary" role="progressbar" aria-label="Ход обучения" aria-valuemin={0} aria-valuemax={100}
                            aria-valuenow={percent === null ? undefined : Math.round(percent)}>
                            <div className="h-full bg-brand transition-[width]" style={{ width: `${percent ?? 0}%` }} />
                        </div>
                    )}
                    {active && progress.batch !== null && progress.batches ? <span className="tabular-nums text-muted-foreground">пакет {progress.batch} / {progress.batches}</span> : null}
                    {version && <span className="text-muted-foreground">mAP50 {formatMetric(version.metrics.mAP50)} · mAP50-95 {formatMetric(version.metrics.mAP50_95)}</span>}
                </div>
            ) : active && (
                <p className="text-xs text-muted-foreground">
                    {run.state === "queued" ? "Ждёт своей очереди: одновременно обучается один запуск." : "Подготовка данных и среды обучения…"}
                </p>
            )}
            {error && <p role="alert" className="text-xs text-destructive" data-testid="training-run-card-error">{error}</p>}
        </li>
    );
}
