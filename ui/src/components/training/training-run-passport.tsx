import { useEffect, useMemo, useRef, type ReactNode } from "react";
import { Loader2 } from "lucide-react";
import type { ModelVersionResponseDto } from "@/api/projects/training/response-dto/model-version-response-dto";
import type { TrainingRunResponseDto } from "@/api/projects/training/response-dto/training-run-response-dto";
import { isActiveTrainingRun, trainingMetricLabels, trainingRunStateLabels, trainingStageLabels } from "@/api/projects/training/training-parameters";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
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
 * «Паспорт запуска» во вкладке «Текущий запуск»: состояние, ход обучения («Эпоха X из Y», «Пакет a из b», полоса, живые метрики,
 * стадия worker), ожидание и подготовка, ошибка, окружение, датасет и классы, «История эпох» — графики «Качество по эпохам» и
 * «Потери по эпохам» за весь прогон, «Параметры запуска» (запрошенные и фактические), «Журнал обучения» (последние сообщения и вся
 * история страницами), «Отменить обучение»; после «Готово» — краткий паспорт версии модели.
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
    const details = useTrainingRunDetails(projectId, run);
    const quality = useMemo(() => qualitySeries(details.epochs), [details.epochs]);
    const losses = useMemo(() => lossSeries(details.epochs), [details.epochs]);
    const logRef = useRef<HTMLDivElement>(null);
    const active = isActiveTrainingRun(run.state);
    const { progress } = run;
    const percent = runPercent(run);
    const error = runError(run);
    const environment = environmentText(run.environment);
    const metrics = Object.entries(progress.metrics).filter(([, value]) => Number.isFinite(value));

    // Последние сообщения видны сразу: окно журнала прокручивается к концу.
    useEffect(() => {
        if (details.logMode === "live" && logRef.current) logRef.current.scrollTop = logRef.current.scrollHeight;
    }, [details.log, details.logMode]);

    return (
        <article className="space-y-5" data-testid="training-run-passport" data-run-id={run.id} data-state={run.state} aria-label="Паспорт запуска">
            <header className="flex flex-wrap items-start justify-between gap-3">
                <div>
                    <h2 className="text-lg font-medium">Паспорт запуска</h2>
                    <p className="mt-1 text-sm text-muted-foreground">
                        {run.dataset.name} · {formatDate(run.job.createdAt)}{run.presetName && ` · пресет «${run.presetName}»`}
                    </p>
                </div>
                <div className="flex flex-wrap items-center gap-2">
                    <Badge variant={run.state === "failed" ? "destructive" : "secondary"} data-testid="training-run-state">{trainingRunStateLabels[run.state]}</Badge>
                    {active && (
                        <Button variant="outline" disabled={busy || run.state === "cancelling"} data-testid="training-cancel" onClick={cancel}>
                            Отменить обучение
                        </Button>
                    )}
                </div>
            </header>

            <section aria-label="Ход обучения" className="space-y-2" data-testid="training-progress">
                {progress.epoch !== null && progress.epochs ? (
                    <>
                        <div className="flex flex-wrap justify-between gap-2 text-sm tabular-nums">
                            <span data-testid="training-progress-epoch">Эпоха {progress.epoch} из {progress.epochs}</span>
                            {progress.batch !== null && progress.batches ? (
                                <span className="text-muted-foreground" data-testid="training-progress-batch">Пакет {progress.batch} из {progress.batches}</span>
                            ) : null}
                        </div>
                    </>
                ) : (
                    <p className="text-sm text-muted-foreground" data-testid="training-progress-waiting">
                        {run.state === "queued"
                            ? "Ждёт освобождения видеокарты: одновременно обучается один запуск."
                            : active
                                ? "Подготовка данных и среды. Метрики появятся после первой эпохи."
                                : "Запуск завершился до получения метрик."}
                    </p>
                )}
                {(active || run.state === "succeeded") && (
                    <div className="h-2 overflow-hidden rounded-full bg-secondary" role="progressbar" aria-label="Ход обучения" aria-valuemin={0} aria-valuemax={100}
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
                    Начало: {run.job.startedAt ? formatDate(run.job.startedAt) : "—"} · Завершение: {run.job.finishedAt ? formatDate(run.job.finishedAt) : "—"}
                </p>
                {environment && <p className="text-xs text-muted-foreground" data-testid="training-environment">Окружение: {environment}</p>}
            </section>

            <Section title="Датасет и классы" testId="training-run-dataset">
                <p className="text-sm">
                    {run.dataset.name} · Train {count(run.dataset.counts.train)} · Validation {count(run.dataset.counts.val)} · Test {count(run.dataset.counts.test)}
                </p>
                <p className="text-xs text-muted-foreground">
                    {run.architecture} · вход {run.input.width} × {run.input.height} · imgsz {run.imageSize}
                </p>
                <ol className="flex flex-wrap gap-x-5 gap-y-1 text-sm">
                    {run.dataset.classes.map(entry => <li key={entry.index}><span className="text-muted-foreground">{entry.index}.</span> {entry.name}</li>)}
                </ol>
            </Section>

            <Section title="История эпох" testId="training-epochs">
                {details.epochs.length ? (
                    <>
                        <div className="grid gap-5 lg:grid-cols-2">
                            {quality.length > 0 && <TrainingEpochChart id="quality" label="Качество по эпохам" series={quality} unit />}
                            {losses.length > 0 && <TrainingEpochChart id="losses" label="Потери по эпохам" series={losses} unit={false} />}
                        </div>
                        <p className="text-xs text-muted-foreground" data-testid="training-epochs-caption">
                            Сохранено эпох: {count(details.epochs.length)}. Графики показывают весь прогон; потери val — пунктиром цвета своей train-серии.
                        </p>
                    </>
                ) : (
                    <p className="text-sm text-muted-foreground">
                        {!details.epochsLoaded ? "Загрузка истории…" : active ? "Ожидаем завершения первой эпохи." : "Для этого запуска история эпох не сохранена."}
                    </p>
                )}
            </Section>

            <details className="border-t pt-4" data-testid="training-run-parameters">
                <summary className="cursor-pointer font-medium">Параметры запуска</summary>
                <div className="mt-3 grid gap-5 sm:grid-cols-2">
                    <ParameterList title="Запрошенные" parameters={run.parameters} />
                    <ParameterList title="Фактические" parameters={run.effectiveParams} empty="Worker ещё не сообщил фактические параметры." />
                </div>
            </details>

            <Section
                title="Журнал обучения"
                testId="training-log"
                action={
                    <div className="flex gap-2">
                        <Button variant={details.logMode === "live" ? "secondary" : "outline"} size="sm" data-testid="training-log-live"
                            onClick={() => details.showLog("live")}>Последние сообщения</Button>
                        <Button variant={details.logMode === "history" ? "secondary" : "outline"} size="sm" data-testid="training-log-history"
                            onClick={() => details.showLog("history")}>Вся история</Button>
                    </div>
                }
            >
                <div ref={logRef} data-testid="training-log-lines" className="max-h-56 overflow-auto rounded-md border bg-muted/20 p-3 font-mono text-xs leading-relaxed" aria-live="off">
                    {details.log.length ? details.log.map(entry => (
                        <p key={entry.sequence} data-kind={entry.kind}
                            className={`whitespace-pre-wrap break-all ${entry.kind === "warning" ? "text-amber-700 dark:text-amber-400" : entry.kind === "stage" || entry.kind === "system" ? "text-muted-foreground" : ""}`}>
                            {entry.message}
                        </p>
                    )) : <p className="font-sans text-muted-foreground">Сообщений пока нет.</p>}
                </div>
                <p className="text-xs text-muted-foreground" data-testid="training-log-caption">
                    {details.logMode === "live"
                        ? `Окно последних ${trainingLogWindow} сообщений обновляется во время обучения. Всего сохранено: ${count(details.logTotal)}.`
                        : `Всего сохранено сообщений: ${count(details.logTotal)}. Страница до ${trainingLogWindow} сообщений.`}
                </p>
                {details.logMode === "history" && details.historyNext !== null && (
                    <Button variant="outline" size="sm" onClick={details.nextLogPage}>Следующая страница журнала</Button>
                )}
            </Section>
            {details.error && <p role="alert" className="text-sm text-destructive">{details.error}</p>}

            {run.resultVersionId && (
                <div className="border-t pt-4" id={`training-version-${run.resultVersionId}`}>
                    {version
                        ? <ModelVersionPassport version={version} epochs={details.epochs} busy={busy} renew={renewVersion} setWorking={setWorking} />
                        : <p className="text-sm text-muted-foreground" data-testid="training-version-missing">{versionDeleted ? "Версия удалена." : "Загрузка паспорта версии…"}</p>}
                </div>
            )}
        </article>
    );
}
