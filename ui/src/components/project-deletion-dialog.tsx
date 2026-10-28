import { useEffect, useState } from "react";
import { projectsApi } from "@/api/projects/projects-api";
import type { ProjectDeletionResponseDto, ProjectDeletionReviewResponseDto } from "@/api/projects/response-dto/project-lifecycle-response-dto";
import type { ProjectResponseDto } from "@/api/projects/response-dto/project-response-dto";
import { useReturnFocus } from "@/components/catalog/catalog-dialogs";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { errorMessage } from "@/lib/format";
import { activeJobKinds } from "@/lib/overview/active-job-view";

export const deletionCleanupNotice = "Проект удалён. Часть файлов пока занята; приложение повторит очистку при следующем запуске.";
const transferActiveText = "Идёт экспорт проекта. Дождитесь его завершения и повторите.";

/** «Обучение · fake:hang», «Живая проверка»; неизвестный вид — как есть. */
function activityTitle(kind: string, label: string | null): string {
    const name = kind === "checks.live" ? "Живая проверка" : activeJobKinds[kind]?.label ?? kind;
    return label ? `${name} · ${label}` : name;
}

/**
 * «Удалить проект с компьютера?»: сначала reviewDeletion — идущие задачи и живая проверка (их отменит «Отменить задачи и удалить»)
 * или экспорт (удаление недоступно); затем delete с ревизией просмотра. Отказ (изменилась ревизия, появились задачи) остаётся в окне,
 * «Проверить снова» перечитывает состав. Пока идёт удаление, окно не закрывается.
 */
export function ProjectDeletionDialog({ project, close, deleted }: {
    project: ProjectResponseDto;
    close: () => void;
    deleted: (result: ProjectDeletionResponseDto) => void;
}) {
    const [review, setReview] = useState<{ value?: ProjectDeletionReviewResponseDto; error?: string; loading: boolean }>({ loading: true });
    const [attempt, setAttempt] = useState(0);
    const [busy, setBusy] = useState(false);
    const [deleteError, setDeleteError] = useState<string>();
    const returnFocus = useReturnFocus();

    useEffect(() => {
        const controller = new AbortController();
        setReview({ loading: true });
        setDeleteError(undefined);
        projectsApi.reviewDeletion({ projectId: project.id }, controller.signal).then(
            value => setReview({ value, loading: false }),
            cause => {
                if (!controller.signal.aborted) setReview({ loading: false, error: errorMessage(cause, "Не удалось проверить задачи проекта. Повторите проверку.") });
            });
        return () => controller.abort();
    }, [project.id, attempt]);

    const current = review.value;
    const withJobs = !!current?.activities.length;

    async function confirm() {
        if (!current) return;
        setBusy(true);
        setDeleteError(undefined);
        try {
            deleted(await projectsApi.delete({ projectId: current.projectId, revision: current.revision, cancelJobs: withJobs }));
        } catch (cause) {
            setDeleteError(errorMessage(cause));
        } finally {
            setBusy(false);
        }
    }

    const name = current?.name ?? project.name;
    return (
        <Dialog open onOpenChange={open => { if (!open && !busy) close(); }}>
            <DialogContent
                data-testid="project-deletion-dialog"
                showCloseButton={!busy}
                className="max-h-[calc(100dvh-3rem)] overflow-y-auto"
                onEscapeKeyDown={event => { if (busy) event.preventDefault(); }}
                onInteractOutside={event => { if (busy) event.preventDefault(); }}
                onCloseAutoFocus={returnFocus}
            >
                <DialogHeader>
                    <DialogTitle>Удалить проект с компьютера?</DialogTitle>
                    <DialogDescription className="break-words" data-testid="project-deletion-text">
                        “{name}” и все его материалы, датасеты, история обучения и обученные модели будут удалены без возможности восстановления. Общие
                        базовые YOLO и файлы других проектов сохранятся. Для сохранения копии сначала экспортируйте проект.
                    </DialogDescription>
                </DialogHeader>
                {review.loading && <p role="status" className="text-sm text-muted-foreground">Проверяем задачи проекта…</p>}
                {current && !review.loading && withJobs && (
                    <div className="space-y-2 text-sm" data-testid="project-deletion-activities">
                        <p className="font-medium">Идут фоновые задачи — удаление их отменит:</p>
                        <ul className="list-disc space-y-1 pl-5">
                            {current.activities.map((activity, index) => (
                                <li key={activity.jobId ?? `${activity.kind}-${index}`} className="break-words" data-testid="project-deletion-activity">
                                    {activityTitle(activity.kind, activity.label)}
                                </li>
                            ))}
                        </ul>
                    </div>
                )}
                <div aria-live="polite">
                    {current?.transferActive && !review.loading && (
                        <Alert variant="destructive"><AlertDescription data-testid="project-deletion-transfer">{transferActiveText}</AlertDescription></Alert>
                    )}
                    {(review.error || deleteError) && (
                        <Alert variant="destructive"><AlertDescription data-testid="project-deletion-error">{review.error ?? deleteError}</AlertDescription></Alert>
                    )}
                </div>
                <DialogFooter>
                    <Button variant="outline" autoFocus disabled={busy} onClick={close}>Отмена</Button>
                    {(review.error || deleteError || current?.transferActive) && (
                        <Button variant="outline" disabled={busy || review.loading} onClick={() => setAttempt(value => value + 1)} data-testid="project-deletion-retry">
                            Проверить снова
                        </Button>
                    )}
                    <Button
                        variant="destructive"
                        data-testid="project-deletion-confirm"
                        disabled={busy || review.loading || !current || current.transferActive || !!deleteError}
                        onClick={() => void confirm()}
                    >
                        {busy ? "Удаление…" : withJobs ? "Отменить задачи и удалить" : "Удалить проект"}
                    </Button>
                </DialogFooter>
            </DialogContent>
        </Dialog>
    );
}
