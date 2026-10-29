import { useCallback, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { datasetsApi } from "@/api/projects/datasets/datasets-api";
import { maxDatasetDescriptionLength, maxDatasetNameLength } from "@/api/projects/datasets/dataset-rules";
import type { DatasetDeletionResultResponseDto, DatasetDeletionReviewResponseDto, DatasetVersionResponseDto } from "@/api/projects/datasets/response-dto/dataset-response-dto";
import { DialogError, GuardedDialog, useReturnFocus } from "@/components/catalog/catalog-dialogs";
import { ReviewDeletionDialog } from "@/components/deletion/review-deletion-dialog";
import { Alert, AlertDescription } from "@/components/ui/alert";
import {
    AlertDialog,
    AlertDialogContent,
    AlertDialogDescription,
    AlertDialogFooter,
    AlertDialogHeader,
    AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Button } from "@/components/ui/button";
import { DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import { count } from "@/lib/datasets/dataset-view";
import { errorMessage } from "@/lib/format";

/** "Edit": the name (1–120) and the description (up to 4000); everything else in the version is immutable. */
export function DatasetEditDialog({ version, busy, error, close, save }: {
    version: DatasetVersionResponseDto;
    busy: boolean;
    error?: string;
    close: () => void;
    save: (values: { name: string; description: string }) => void;
}) {
    const { t } = useTranslation("datasets");
    const [name, setName] = useState(version.name);
    const [description, setDescription] = useState(version.description);

    function submit(event: FormEvent) {
        event.preventDefault();
        if (!busy && name.trim()) save({ name: name.trim(), description: description.trim() });
    }

    return (
        <GuardedDialog busy={busy} close={close} testId="dataset-edit-dialog">
            <DialogHeader>
                <DialogTitle>{t("edit.title")}</DialogTitle>
                <DialogDescription>{t("edit.description")}</DialogDescription>
            </DialogHeader>
            <form className="space-y-4" onSubmit={submit}>
                <div className="space-y-2">
                    <Label htmlFor="dataset-edit-name">{t("edit.name")}</Label>
                    <Input id="dataset-edit-name" data-testid="dataset-edit-name" autoFocus required maxLength={maxDatasetNameLength} value={name}
                        onChange={event => setName(event.target.value)} />
                </div>
                <div className="space-y-2">
                    <Label htmlFor="dataset-edit-description">{t("edit.descriptionLabel")}</Label>
                    <Textarea id="dataset-edit-description" data-testid="dataset-edit-description" className="h-24 resize-none" maxLength={maxDatasetDescriptionLength}
                        value={description} onChange={event => setDescription(event.target.value)} />
                </div>
                <DialogError error={error} />
                <DialogFooter>
                    <Button type="button" variant="outline" disabled={busy} onClick={close}>{t("edit.cancel")}</Button>
                    <Button type="submit" data-testid="dataset-edit-save" disabled={busy || !name.trim()}>{busy ? t("edit.saving") : t("edit.save")}</Button>
                </DialogFooter>
            </form>
        </GuardedDialog>
    );
}

/**
 * "Delete the dataset for good?" in two phases: what goes (images, operations) and blockers, then deletion with the revision of the
 * review. The version passport stays for models.
 */
export function DatasetDeletionDialog({ projectId, version, close, deleted }: {
    projectId: string;
    version: DatasetVersionResponseDto;
    close: () => void;
    deleted: (result: DatasetDeletionResultResponseDto) => void;
}) {
    const { t } = useTranslation("datasets");
    const review = useCallback((signal: AbortSignal) => datasetsApi.reviewDeletion({ projectId, versionId: version.id }, signal), [projectId, version.id]);
    const remove = useCallback((current: DatasetDeletionReviewResponseDto) => datasetsApi.delete({ projectId, versionId: version.id, revision: current.revision }),
        [projectId, version.id]);
    return (
        <ReviewDeletionDialog
                area="datasets"
            title={t("deletion.title")}
            description={t("deletion.description", { name: version.name })}
            testId="dataset-deletion-dialog"
            review={review}
            remove={remove}
            deleted={deleted}
            close={close}
            renderReview={current => <p data-testid="dataset-deletion-counts">{t("deletion.counts", { samples: count(current.samples), operations: count(current.operations) })}</p>}
            blockersTitle={<p className="font-medium">{t("deletion.blockers")}</p>}
            busyText={t("deletion.deleting")}
        />
    );
}

/** "Delete the record from the history?": only the operation record; the dataset, source images and saved ZIP files stay. */
export function DatasetOperationDeletionDialog({ projectId, operationId, name, close, deleted }: {
    projectId: string;
    operationId: string;
    name: string;
    close: () => void;
    deleted: (operationId: string) => void;
}) {
    const { t } = useTranslation("datasets");
    const [busy, setBusy] = useState(false);
    const [error, setError] = useState<string>();
    const returnFocus = useReturnFocus();

    async function commit() {
        if (busy) return;
        setBusy(true);
        setError(undefined);
        try {
            await datasetsApi.deleteOperation({ projectId, operationId });
            deleted(operationId);
        } catch (cause) {
            setError(errorMessage(cause, t("deletion.failed")));
        } finally {
            setBusy(false);
        }
    }

    return (
        <AlertDialog open onOpenChange={open => { if (!open && !busy) close(); }}>
            <AlertDialogContent className="max-h-[85vh] overflow-auto" data-testid="dataset-operation-deletion-dialog"
                onEscapeKeyDown={event => { if (busy) event.preventDefault(); }} onCloseAutoFocus={returnFocus}>
                <AlertDialogHeader>
                    <AlertDialogTitle>{t("deletion.operationTitle")}</AlertDialogTitle>
                    <AlertDialogDescription className="break-words">
                        {t("deletion.operationText", { name })}
                    </AlertDialogDescription>
                </AlertDialogHeader>
                <div aria-live="polite">
                    {error && <Alert variant="destructive"><AlertDescription>{error}</AlertDescription></Alert>}
                </div>
                <AlertDialogFooter>
                    <Button variant="outline" autoFocus disabled={busy} onClick={close}>{t("edit.cancel")}</Button>
                    <Button variant="destructive" disabled={busy} data-testid="dataset-operation-deletion-confirm" onClick={() => void commit()}>
                        {busy ? t("deletion.deleting") : t("deletion.confirm")}
                    </Button>
                </AlertDialogFooter>
            </AlertDialogContent>
        </AlertDialog>
    );
}
