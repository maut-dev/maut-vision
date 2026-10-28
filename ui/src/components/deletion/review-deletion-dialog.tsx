import { useEffect, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { useReturnFocus } from "@/components/catalog/catalog-dialogs";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { blockerText, type Blocker, type BlockerArea } from "@/lib/errors";
import { errorMessage } from "@/lib/format";

export interface DeletionReview {
    blockers: readonly Blocker[];
}

/**
 * Удаление навсегда в две фазы (изображения, фоны): просмотр состава и блокировок, затем удаление с ревизией просмотра.
 * Ошибка удаления (например, изменился состав) требует «Проверить снова». Пока идёт удаление, окно не закрывается;
 * фокус после закрытия возвращается к открывшему элементу.
 */
export function ReviewDeletionDialog<R extends DeletionReview, D>({ area, title, description, testId, review, remove, deleted, close, renderReview, blockersTitle, blockersNote, busyText }: {
    /** Which dialog lists the blockers: the same code reads differently in each. */
    area: BlockerArea;
    title: string;
    description: ReactNode;
    testId: string;
    /** Чтение состава; меняется — перечитывается. */
    review: (signal: AbortSignal) => Promise<R>;
    remove: (review: R) => Promise<D>;
    deleted: (result: D) => void;
    close: () => void;
    renderReview: (review: R) => ReactNode;
    blockersTitle: ReactNode;
    /** Пояснение после списка блокировок. */
    blockersNote?: ReactNode;
    busyText: string;
}) {
    const { t } = useTranslation();
    const [state, setState] = useState<{ review?: R; error?: string; loading: boolean }>({ loading: true });
    const [attempt, setAttempt] = useState(0);
    const [busy, setBusy] = useState(false);
    const [deleteError, setDeleteError] = useState<string>();
    const returnFocus = useReturnFocus();

    useEffect(() => {
        const controller = new AbortController();
        setState({ loading: true });
        setDeleteError(undefined);
        review(controller.signal).then(
            value => setState({ review: value, loading: false }),
            cause => {
                if (!controller.signal.aborted) setState({ loading: false, error: errorMessage(cause, t("deletion.reviewFailed")) });
            });
        return () => controller.abort();
    }, [review, attempt]);

    async function confirm() {
        const current = state.review;
        if (!current) return;
        setBusy(true);
        setDeleteError(undefined);
        try {
            deleted(await remove(current));
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
                data-testid={testId}
                showCloseButton={!busy}
                className="max-h-[calc(100dvh-3rem)] overflow-y-auto"
                onEscapeKeyDown={event => { if (busy) event.preventDefault(); }}
                onInteractOutside={event => { if (busy) event.preventDefault(); }}
                onCloseAutoFocus={returnFocus}
            >
                <DialogHeader>
                    <DialogTitle>{title}</DialogTitle>
                    <DialogDescription className="break-words">{description}</DialogDescription>
                </DialogHeader>
                {state.loading && <p role="status" className="text-sm text-muted-foreground">{t("deletion.checking")}</p>}
                {current && !state.loading && (
                    <div className="space-y-3 text-sm">
                        {renderReview(current)}
                        {blocked && (
                            <Alert variant="destructive">
                                <AlertDescription>
                                    {blockersTitle}
                                    <ul className="mt-2 list-disc space-y-1 pl-5">
                                        {current.blockers.map(blocker => <li key={blocker.code} className="break-words">{blockerText(area, blocker)}</li>)}
                                    </ul>
                                    {blockersNote}
                                </AlertDescription>
                            </Alert>
                        )}
                    </div>
                )}
                <div aria-live="polite">
                    {(state.error || deleteError) && <Alert variant="destructive"><AlertDescription>{state.error ?? deleteError}</AlertDescription></Alert>}
                </div>
                <DialogFooter>
                    <Button variant="outline" autoFocus disabled={busy} onClick={close}>{t("app.cancel")}</Button>
                    <Button variant="outline" disabled={busy || state.loading} onClick={() => setAttempt(value => value + 1)}>{t("common.checkAgain")}</Button>
                    <Button
                        variant="destructive"
                        data-testid={`${testId.replace(/-dialog$/, "")}-confirm`}
                        disabled={busy || state.loading || !current || blocked || !!deleteError}
                        onClick={() => void confirm()}
                    >
                        {busy ? busyText : t("deletion.confirm")}
                    </Button>
                </DialogFooter>
            </DialogContent>
        </Dialog>
    );
}
