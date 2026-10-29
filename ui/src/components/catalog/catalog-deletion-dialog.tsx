import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { classesApi } from "@/api/projects/catalog/classes-api";
import type { CatalogTargetKind, DeletionResultResponseDto, DeletionReviewResponseDto } from "@/api/projects/catalog/response-dto/deletion-review-response-dto";
import { variantsApi } from "@/api/projects/catalog/variants-api";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { errorMessage } from "@/lib/format";
import { useReturnFocus } from "./catalog-dialogs";
import { blockerText } from "@/lib/errors";
import { i18n } from "@/i18n";
import "@/i18n/sections/catalog";

export interface DeletionTarget {
    kind: CatalogTargetKind;
    id: string;
    /** The label of the target in the dialog: the review sends no name for an image. */
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
 * Permanent deletion: a review of the contents (counters and blockers), then deletion with the revision of that review.
 * A failed deletion (the contents changed, a blocker appeared) needs "Check again".
 */
export function CatalogDeletionDialog({ projectId, target, close, deleted }: {
    projectId: string;
    target: DeletionTarget;
    close: () => void;
    deleted: (result: DeletionResultResponseDto) => void;
}) {
    const { t } = useTranslation("catalog");
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
                    setState({ loading: false, error: errorMessage(cause, i18n.t("catalog:deletion.reviewFailed")) });
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
                    <DialogTitle>{t("deletion.title")}</DialogTitle>
                    <DialogDescription className="break-words">{target.name}</DialogDescription>
                </DialogHeader>
                {state.loading && <p role="status" className="text-sm text-muted-foreground">{t("deletion.checking")}</p>}
                {current && !state.loading && (
                    <div className="space-y-3 text-sm">
                        <dl className="grid grid-cols-[1fr_auto] gap-2" data-testid="deletion-counts">
                            <dt>{t("deletion.classes")}</dt><dd className="text-right tabular-nums" data-count="classes">{current.classes}</dd>
                            <dt>{t("deletion.classVariants")}</dt><dd className="text-right tabular-nums" data-count="classVariants">{current.classVariants}</dd>
                            <dt>{t("deletion.assets")}</dt><dd className="text-right tabular-nums" data-count="assets">{current.assets}</dd>
                        </dl>
                        <p className="text-muted-foreground">
                            {t("deletion.note")}
                        </p>
                        {blocked && (
                            <Alert variant="destructive">
                                <AlertDescription>
                                    <p>{t("deletion.blocked")}</p>
                                    <ul className="mt-2 list-disc space-y-1 pl-5">
                                        {current.blockers.map(blocker => <li key={blocker.code} className="break-words">{blockerText("catalog", blocker)}</li>)}
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
                    <Button variant="outline" autoFocus disabled={busy} onClick={close}>{t("dialog.cancel")}</Button>
                    <Button variant="outline" disabled={busy || state.loading} onClick={() => setAttempt(value => value + 1)}>{t("deletion.checkAgain")}</Button>
                    <Button
                        variant="destructive"
                        data-testid="deletion-confirm"
                        disabled={busy || state.loading || !current || blocked || !!deleteError}
                        onClick={() => void confirm()}
                    >
                        {busy ? t("deletion.deleting") : t("deletion.confirm")}
                    </Button>
                </DialogFooter>
            </DialogContent>
        </Dialog>
    );
}
