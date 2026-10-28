import { FolderOpen, Loader2, RotateCcw, Trash2 } from "lucide-react";
import { datasetTexts, integrityLabels } from "@/api/projects/datasets/dataset-rules";
import {
    parseDatasetBuildResult,
    parseDatasetExportResult,
    parseDatasetVerifyResult,
    type DatasetOperationResponseDto,
} from "@/api/projects/datasets/response-dto/dataset-operation-response-dto";
import type { DatasetVersionResponseDto } from "@/api/projects/datasets/response-dto/dataset-response-dto";
import { jobStatusLabels } from "@/components/jobs/job-run";
import { Button } from "@/components/ui/button";
import {
    count,
    duration,
    isOperationActive,
    issueRoleLabels,
    operationEndText,
    operationProgress,
    operationTitles,
    splitLabel,
} from "@/lib/datasets/dataset-view";
import { formatDate, megabytes } from "@/lib/format";

/**
 * Карточка операции во вкладках «Сборка», «Проверки», «Экспорты»: состояние, датасет, дата, «Собрано изображений / Проверено
 * файлов / Экспортировано изображений: p из t» с полосой по событиям job.changed, длительность, «Обнаруженные проблемы (n)»,
 * файл экспорта; «Отменить» у идущей, «Настроить повтор» / «Повторить» / «Сохранить ещё раз» и «Удалить из истории» у
 * завершённой, «Открыть датасет», если версия есть в списке. version — текущая версия (название, архив, целостность).
 */
export function DatasetOperationCard({ operation, version, busy, cancel, repeat, remove, openDataset }: {
    operation: DatasetOperationResponseDto;
    version: DatasetVersionResponseDto | undefined;
    busy: boolean;
    cancel: () => void;
    repeat: () => void;
    remove: () => void;
    openDataset: () => void;
}) {
    const { job, kind } = operation;
    const active = isOperationActive(job);
    const progress = operationProgress(operation);
    const status = job.cancelRequested && active ? "Отмена…" : jobStatusLabels[job.status];
    const name = version?.name ?? operation.versionName;
    const elapsed = duration(job);
    const endText = operationEndText(operation);
    const build = parseDatasetBuildResult(job);
    const verify = parseDatasetVerifyResult(job);
    const exported = parseDatasetExportResult(job);
    const fileName = exported?.fileName ?? operation.fileName;
    const archived = !!version?.archivedAt;
    const repeatLabel = kind === "build" ? "Настроить повтор" : kind === "export" && job.status === "succeeded" ? "Сохранить ещё раз" : "Повторить";
    const repeatBlocked = kind === "build" ? !operation.specification : !version || archived || (kind === "export" && version.integrityState === "corrupt");
    const canOpen = !!version && (kind !== "build" || job.status === "succeeded");
    return (
        <article className="space-y-3 rounded-md border bg-card p-4" data-dataset-operation-id={job.id} data-kind={kind} data-status={job.status}
            aria-label={`${operationTitles[kind]}: ${name}`}>
            <header className="flex flex-wrap items-start justify-between gap-2">
                <div className="min-w-0">
                    <h3 className="flex items-center gap-2 font-medium break-words" data-testid="operation-title">
                        {active && <Loader2 className="size-3.5 shrink-0 animate-spin" aria-hidden />}
                        {kind === "build" ? name : operationTitles[kind]} · {status}
                    </h3>
                    <p className="mt-1 text-sm break-words text-muted-foreground">
                        {kind === "build" ? operationTitles.build : name}{!version && !active && job.status === "succeeded" && " · версия удалена или недоступна"} · {formatDate(job.createdAt)}
                    </p>
                </div>
                <div className="flex flex-wrap gap-2">
                    {canOpen && (
                        <Button variant="outline" size="sm" data-testid="operation-open-dataset" aria-label={`Открыть датасет: ${name}`} onClick={openDataset}>
                            <FolderOpen />Открыть датасет
                        </Button>
                    )}
                    {active ? (
                        <Button variant="outline" size="sm" disabled={busy || job.cancelRequested} data-testid="operation-cancel" onClick={cancel}>
                            {job.cancelRequested ? "Отменяем…" : "Отменить"}
                        </Button>
                    ) : (
                        <>
                            <Button variant="outline" size="sm" disabled={busy || repeatBlocked} data-testid="operation-repeat" aria-label={`${repeatLabel}: ${name}`} onClick={repeat}>
                                <RotateCcw />{repeatLabel}
                            </Button>
                            <Button variant="ghost" size="sm" disabled={busy} data-testid="operation-delete" aria-label={`Удалить из истории: ${name}`} onClick={remove}>
                                <Trash2 />Удалить из истории
                            </Button>
                        </>
                    )}
                </div>
            </header>
            <p className="text-sm tabular-nums" data-testid="operation-progress-text">
                {progress.label}: {count(progress.processed)}{progress.total !== null && ` из ${count(progress.total)}`}
                {progress.percent !== null && active && ` · ${progress.percent}%`}
            </p>
            {(active || job.status === "succeeded") && (
                <div className="h-1.5 overflow-hidden rounded-full bg-secondary" role="progressbar" aria-label={`Прогресс: ${operationTitles[kind]}`}
                    aria-valuemin={0} aria-valuemax={100} aria-valuenow={progress.percent ?? undefined} data-testid="operation-progress">
                    <div className={progress.percent === null ? "h-full w-1/3 animate-pulse bg-brand" : "h-full bg-brand transition-[width]"}
                        style={progress.percent === null ? undefined : { width: `${progress.percent}%` }} />
                </div>
            )}
            {kind === "export" && fileName && (
                <p className="text-sm break-all" data-testid="operation-file">Файл: {fileName}{exported && exported.bytes > 0 && ` · ${megabytes(exported.bytes)}`}</p>
            )}
            {kind === "export" && active && <p role="status" className="text-xs text-muted-foreground">Изображения добавляются в ZIP. Файл будет готов после завершения экспорта.</p>}
            {kind === "verify" && verify?.state && (
                <p className={`text-sm ${verify.state === "corrupt" ? "text-destructive" : ""}`} data-testid="operation-integrity">Целостность: {integrityLabels[verify.state]}</p>
            )}
            {job.startedAt && (
                <p className="text-xs text-muted-foreground">
                    Начало: {formatDate(job.startedAt)}{job.finishedAt && ` · Завершение: ${formatDate(job.finishedAt)}`}{elapsed && ` · Длительность: ${elapsed}`}
                </p>
            )}
            {verify && verify.issueCount > 0 && (
                <div className="space-y-2 rounded-md border p-3 text-sm" data-testid="operation-issues">
                    <h4 className="font-medium">Обнаруженные проблемы ({count(verify.issueCount)})</h4>
                    <ul className="max-h-40 space-y-1 overflow-auto" aria-label="Проблемы целостности">
                        {verify.issues.map((issue, index) => (
                            <li key={`${issue.role}:${issue.sampleId}:${index}`} className="break-words">
                                {issueRoleLabels[issue.role]}{issue.sampleId && ` · кадр ${issue.sampleId}`}{issue.split && ` · ${splitLabel(issue.split)}`} · {issue.message}
                            </li>
                        ))}
                    </ul>
                    {verify.issueCount > verify.issues.length && <p className="text-xs text-muted-foreground">Показаны первые {count(verify.issues.length)}.</p>}
                    <p className="text-xs text-muted-foreground">{datasetTexts.integrityHelp}</p>
                </div>
            )}
            {endText && <p role="alert" className="text-sm break-words text-destructive" data-testid="operation-error">{endText}</p>}
            <details className="text-xs text-muted-foreground">
                <summary className="cursor-pointer">{kind === "build" ? "Настройки и результат" : "Результат и детали"}</summary>
                <div className="mt-2 space-y-2">
                    {kind === "build" && operation.specification && (
                        <>
                            <p>
                                Seed {operation.specification.seed} · групп {operation.specification.groupIds.length} · классов {operation.specification.classIds.length}
                                · Validation {Math.round(operation.specification.valRatio * 100)}% · Test {Math.round(operation.specification.testRatio * 100)}%
                            </p>
                            <p>Повтор переносит настройки в форму. Состав текущих исходников проверяется заново.</p>
                        </>
                    )}
                    {build && (
                        <p>
                            Train {count(build.counts.train)} · Validation {count(build.counts.val)} · Test {count(build.counts.test)} · ссылок на файлы проекта: {count(build.linked)}
                            · копий: {count(build.copied)} · преобразовано в PNG: {count(build.converted)}
                        </p>
                    )}
                    {kind === "verify" && <p>Счётчик включает manifest.json, data.yaml и пару файлов (изображение и разметка) каждого кадра.</p>}
                    {kind === "export" && <p>ZIP записывается в выбранное вами место. «Сохранить ещё раз» создаёт новый экспорт и позволяет выбрать новое место.</p>}
                    {job.status === "succeeded" && kind === "verify" && <p>Проверка завершена. Состояние целостности версии обновлено.</p>}
                    {job.status === "succeeded" && kind === "export" && <p>Экспорт завершён в выбранное место. Наличие внешнего ZIP-файла после завершения не проверяется.</p>}
                    <p className="break-all">Версия: {operation.versionId}</p>
                </div>
            </details>
        </article>
    );
}
