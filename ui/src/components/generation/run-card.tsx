import { Images, Loader2, RotateCcw, Trash2 } from "lucide-react";
import type { BackgroundSetResponseDto } from "@/api/projects/backgrounds/response-dto/background-set-response-dto";
import type { CatalogWorkspaceResponseDto } from "@/api/projects/catalog/response-dto/catalog-workspace-response-dto";
import type { GenerationRunResponseDto } from "@/api/projects/generation/response-dto/generation-run-response-dto";
import { jobStatusLabels } from "@/components/jobs/job-run";
import { Button } from "@/components/ui/button";
import { formatDate } from "@/lib/format";
import { isRunActive, runErrorText, runProgress, runSummary } from "@/lib/generation/run-state";
import { RecipeDetails } from "./recipe-details";

/**
 * Карточка запуска: состояние и дата, «P / R изображений · X%» с полосой прогресса по событиям job.changed, «Открыть
 * изображения» (группа результата), «Отменить» у идущего, «Настроить повтор» и «Удалить» у завершённого, текст ошибки, итог
 * и «Настройки этого запуска».
 */
export function RunCard({ run, workspace, sets, busy, openImages, cancel, repeat, remove }: {
    run: GenerationRunResponseDto;
    workspace: CatalogWorkspaceResponseDto | undefined;
    sets: readonly BackgroundSetResponseDto[];
    busy: boolean;
    openImages: (groupId: string) => void;
    cancel: () => void;
    repeat: () => void;
    remove: () => void;
}) {
    const { job, recipe } = run;
    const active = isRunActive(job);
    const progress = runProgress(run);
    const error = runErrorText(run);
    const status = job.cancelRequested && active ? "Отмена…" : jobStatusLabels[job.status];
    return (
        <article className="space-y-3 rounded-xl border bg-card p-5" data-generation-run-id={job.id} data-status={job.status} aria-label={`Запуск ${recipe.name}`}>
            <div className="flex flex-wrap items-start justify-between gap-3">
                <div className="min-w-0">
                    <h3 className="font-medium break-words">{recipe.name}</h3>
                    <p className="mt-1 flex items-center gap-2 text-sm" data-testid="run-status">
                        {active && <Loader2 className="size-3.5 animate-spin" aria-hidden />}
                        <span>{status} · {formatDate(job.createdAt)}</span>
                    </p>
                </div>
                <div className="flex flex-wrap gap-2">
                    {progress.outputGroupId && (
                        <Button variant="outline" size="sm" data-testid="run-open-images" aria-label={`Открыть изображения запуска: ${recipe.name}`}
                            onClick={() => openImages(progress.outputGroupId!)}>
                            <Images />Открыть изображения
                        </Button>
                    )}
                    {active ? (
                        <Button variant="outline" size="sm" disabled={busy || job.cancelRequested} data-testid="run-cancel" onClick={cancel}>
                            {job.cancelRequested ? "Отменяем…" : "Отменить"}
                        </Button>
                    ) : (
                        <>
                            <Button variant="outline" size="sm" disabled={busy} data-testid="run-repeat" aria-label={`Настроить повтор: ${recipe.name}`} onClick={repeat}>
                                <RotateCcw />Настроить повтор
                            </Button>
                            <Button variant="ghost" size="sm" className="text-destructive hover:text-destructive" disabled={busy} data-testid="run-delete"
                                aria-label={`Удалить запуск: ${recipe.name}`} onClick={remove}>
                                <Trash2 />Удалить
                            </Button>
                        </>
                    )}
                </div>
            </div>
            <p className="text-sm tabular-nums" data-testid="run-summary">{runSummary(run)}</p>
            <div className="h-1.5 overflow-hidden rounded-full bg-secondary" role="progressbar" aria-label={`Прогресс генерации ${recipe.name}`}
                aria-valuemin={0} aria-valuemax={100} aria-valuenow={progress.percent} data-testid="run-progress">
                <div className={`h-full bg-brand transition-[width] ${job.status === "failed" ? "bg-destructive" : ""}`} style={{ width: `${progress.percent}%` }} />
            </div>
            {error && <p role="status" className="text-sm text-destructive" data-testid="run-error">{error}</p>}
            <details className="rounded-md border px-3 py-2" data-testid="run-settings">
                <summary className="cursor-pointer text-sm">Настройки этого запуска</summary>
                <div className="mt-3 space-y-3">
                    <p className="text-xs tabular-nums">Seed: {run.seed} · количество: {run.requestedCount.toLocaleString("ru-RU")} · версия пресета: {recipe.revision}</p>
                    {run.outputGroupName && <p className="text-xs break-words text-muted-foreground">Группа результата: {run.outputGroupName}</p>}
                    <RecipeDetails recipe={recipe} workspace={workspace} sets={sets} />
                    <p className="text-xs text-muted-foreground">Настройки зафиксированы при запуске. Названия и доступность источников показаны по текущему проекту.</p>
                </div>
            </details>
        </article>
    );
}
