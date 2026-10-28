import { useEffect, useState } from "react";
import { classesApi } from "@/api/projects/catalog/classes-api";
import type { CatalogTargetKind, DeletionResultResponseDto, DeletionReviewResponseDto } from "@/api/projects/catalog/response-dto/deletion-review-response-dto";
import { variantsApi } from "@/api/projects/catalog/variants-api";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { errorMessage } from "@/lib/format";
import { useReturnFocus } from "./catalog-dialogs";

export interface DeletionTarget {
    kind: CatalogTargetKind;
    id: string;
    /** Подпись цели в диалоге: у изображения review не присылает названия. */
    name: string;
}

function review(projectId: string, target: DeletionTarget, signal: AbortSignal): Promise<DeletionReviewResponseDto> {
    switch (target.kind) {
        case "variant": return variantsApi.reviewDeletion({ projectId, variantId: target.id }, signal);
        case "class": return classesApi.reviewDeletion({ projectId, classId: target.id }, signal);
        case "classVariant": return classesApi.reviewVariantDeletion({ projectId, classVariantId: target.id }, signal);
        case "asset": return classesApi.reviewAssetDeletion({ projectId, assetId: target.id }, signal);
    }
}

function remove(projectId: string, target: DeletionTarget, reviewRevision: string): Promise<DeletionResultResponseDto> {
    switch (target.kind) {
        case "variant": return variantsApi.delete({ projectId, variantId: target.id, reviewRevision });
        case "class": return classesApi.delete({ projectId, classId: target.id, reviewRevision });
        case "classVariant": return classesApi.deleteVariant({ projectId, classVariantId: target.id, reviewRevision });
        case "asset": return classesApi.deleteAsset({ projectId, assetId: target.id, reviewRevision });
    }
}

/**
 * Удаление навсегда: просмотр состава (счётчики и блокировки) → удаление с ревизией этого просмотра.
 * Ошибка удаления (состав изменился, появилась блокировка) требует «Проверить снова».
 */
export function CatalogDeletionDialog({ projectId, target, close, deleted }: {
    projectId: string;
    target: DeletionTarget;
    close: () => void;
    deleted: (result: DeletionResultResponseDto) => void;
}) {
    const [state, setState] = useState<{ review?: DeletionReviewResponseDto; error?: string; loading: boolean }>({ loading: true });
    const [attempt, setAttempt] = useState(0);
    const [busy, setBusy] = useState(false);
    const [deleteError, setDeleteError] = useState<string>();
    const returnFocus = useReturnFocus();

    useEffect(() => {
        const controller = new AbortController();
        setState({ loading: true });
        setDeleteError(undefined);
        review(projectId, target, controller.signal).then(
            result => setState({ review: result, loading: false }),
            cause => {
                if (!controller.signal.aborted) {
                    setState({ loading: false, error: errorMessage(cause, "Не удалось проверить зависимости. Повторите проверку.") });
                }
            });
        return () => controller.abort();
    }, [projectId, target, attempt]);

    async function confirm() {
        if (!state.review) return;
        setBusy(true);
        setDeleteError(undefined);
        try {
            deleted(await remove(projectId, target, state.review.revision));
        } catch (cause) {
            setDeleteError(errorMessage(cause));
        } finally {
            setBusy(false);
        }
    }

    const current = state.review;
    const blocked = !!current?.blockers.length;
    return (
        <Dialog open onOpenChange={open => { if (!open && !busy) close(); }}>
            <DialogContent
                data-testid="deletion-dialog"
                showCloseButton={!busy}
                className="max-h-[calc(100dvh-3rem)] overflow-y-auto"
                onEscapeKeyDown={event => { if (busy) event.preventDefault(); }}
                onInteractOutside={event => { if (busy) event.preventDefault(); }}
                onCloseAutoFocus={returnFocus}
            >
                <DialogHeader>
                    <DialogTitle>Удалить навсегда?</DialogTitle>
                    <DialogDescription className="break-words">{target.name}</DialogDescription>
                </DialogHeader>
                {state.loading && <p role="status" className="text-sm text-muted-foreground">Проверяем материалы и зависимости…</p>}
                {current && !state.loading && (
                    <div className="space-y-3 text-sm">
                        <dl className="grid grid-cols-[1fr_auto] gap-2" data-testid="deletion-counts">
                            <dt>Классы</dt><dd className="text-right tabular-nums" data-count="classes">{current.classes}</dd>
                            <dt>Варианты классов</dt><dd className="text-right tabular-nums" data-count="classVariants">{current.classVariants}</dd>
                            <dt>Исходные изображения</dt><dd className="text-right tabular-nums" data-count="assets">{current.assets}</dd>
                        </dl>
                        <p className="text-muted-foreground">
                            Материалы будут удалены с компьютера. Это действие нельзя отменить. Готовые датасеты и модели сохранят свои снимки материалов и карту классов.
                        </p>
                        {blocked && (
                            <Alert variant="destructive">
                                <AlertDescription>
                                    <p>Удаление недоступно. Сначала завершите задачи или измените зависимости:</p>
                                    <ul className="mt-2 list-disc space-y-1 pl-5">
                                        {current.blockers.map(blocker => <li key={blocker.code} className="break-words">{blocker.message}</li>)}
                                    </ul>
                                </AlertDescription>
                            </Alert>
                        )}
                    </div>
                )}
                <div aria-live="polite">
                    {(state.error || deleteError) && (
                        <Alert variant="destructive"><AlertDescription>{state.error ?? deleteError}</AlertDescription></Alert>
                    )}
                </div>
                <DialogFooter>
                    <Button variant="outline" autoFocus disabled={busy} onClick={close}>Отмена</Button>
                    <Button variant="outline" disabled={busy || state.loading} onClick={() => setAttempt(value => value + 1)}>Проверить снова</Button>
                    <Button
                        variant="destructive"
                        data-testid="deletion-confirm"
                        disabled={busy || state.loading || !current || blocked || !!deleteError}
                        onClick={() => void confirm()}
                    >
                        {busy ? "Удаление…" : "Удалить навсегда"}
                    </Button>
                </DialogFooter>
            </DialogContent>
        </Dialog>
    );
}
