import { useCallback, useState, type FormEvent } from "react";
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

/** «Изменить»: название (1–120) и описание (до 4000); остальное в версии неизменно. */
export function DatasetEditDialog({ version, busy, error, close, save }: {
    version: DatasetVersionResponseDto;
    busy: boolean;
    error?: string;
    close: () => void;
    save: (values: { name: string; description: string }) => void;
}) {
    const [name, setName] = useState(version.name);
    const [description, setDescription] = useState(version.description);

    function submit(event: FormEvent) {
        event.preventDefault();
        if (!busy && name.trim()) save({ name: name.trim(), description: description.trim() });
    }

    return (
        <GuardedDialog busy={busy} close={close} testId="dataset-edit-dialog">
            <DialogHeader>
                <DialogTitle>Изменить датасет</DialogTitle>
                <DialogDescription>Меняются только название и описание. Кадры, разметка и порядок классов версии неизменны.</DialogDescription>
            </DialogHeader>
            <form className="space-y-4" onSubmit={submit}>
                <div className="space-y-2">
                    <Label htmlFor="dataset-edit-name">Название</Label>
                    <Input id="dataset-edit-name" data-testid="dataset-edit-name" autoFocus required maxLength={maxDatasetNameLength} value={name}
                        onChange={event => setName(event.target.value)} />
                </div>
                <div className="space-y-2">
                    <Label htmlFor="dataset-edit-description">Описание</Label>
                    <Textarea id="dataset-edit-description" data-testid="dataset-edit-description" className="h-24 resize-none" maxLength={maxDatasetDescriptionLength}
                        value={description} onChange={event => setDescription(event.target.value)} />
                </div>
                <DialogError error={error} />
                <DialogFooter>
                    <Button type="button" variant="outline" disabled={busy} onClick={close}>Отмена</Button>
                    <Button type="submit" data-testid="dataset-edit-save" disabled={busy || !name.trim()}>{busy ? "Сохранение…" : "Сохранить"}</Button>
                </DialogFooter>
            </form>
        </GuardedDialog>
    );
}

/**
 * «Удалить датасет навсегда?» в две фазы: состав (изображений, операций) и блокировки, затем
 * удаление с ревизией состава. Паспорт версии сохраняется для моделей.
 */
export function DatasetDeletionDialog({ projectId, version, close, deleted }: {
    projectId: string;
    version: DatasetVersionResponseDto;
    close: () => void;
    deleted: (result: DatasetDeletionResultResponseDto) => void;
}) {
    const review = useCallback((signal: AbortSignal) => datasetsApi.reviewDeletion({ projectId, versionId: version.id }, signal), [projectId, version.id]);
    const remove = useCallback((current: DatasetDeletionReviewResponseDto) => datasetsApi.delete({ projectId, versionId: version.id, revision: current.revision }),
        [projectId, version.id]);
    return (
        <ReviewDeletionDialog
                area="datasets"
            title="Удалить датасет навсегда?"
            description={<>{version.name}. Копии изображений, разметка и история проверок и экспорта этой версии будут удалены. Исходные изображения и паспорта обученных моделей сохранятся. Повторная проверка этого датасета станет недоступна. Восстановить удалённое в приложении нельзя.</>}
            testId="dataset-deletion-dialog"
            review={review}
            remove={remove}
            deleted={deleted}
            close={close}
            renderReview={current => <p data-testid="dataset-deletion-counts">Изображений: {count(current.samples)} · операций: {count(current.operations)}</p>}
            blockersTitle={<p className="font-medium">Сначала завершите или отмените:</p>}
            busyText="Удаляем…"
        />
    );
}

/** «Удалить запись из истории?»: только запись операции; датасет, исходные изображения и сохранённые ZIP остаются. */
export function DatasetOperationDeletionDialog({ projectId, operationId, name, close, deleted }: {
    projectId: string;
    operationId: string;
    name: string;
    close: () => void;
    deleted: (operationId: string) => void;
}) {
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
            setError(errorMessage(cause, "Не удалось удалить. Обновите список и повторите действие."));
        } finally {
            setBusy(false);
        }
    }

    return (
        <AlertDialog open onOpenChange={open => { if (!open && !busy) close(); }}>
            <AlertDialogContent className="max-h-[85vh] overflow-auto" data-testid="dataset-operation-deletion-dialog"
                onEscapeKeyDown={event => { if (busy) event.preventDefault(); }} onCloseAutoFocus={returnFocus}>
                <AlertDialogHeader>
                    <AlertDialogTitle>Удалить запись из истории?</AlertDialogTitle>
                    <AlertDialogDescription className="break-words">
                        {name}. Запись операции будет удалена. Готовые датасеты, исходные изображения и сохранённые вами ZIP-файлы останутся. Восстановить удалённое в приложении нельзя.
                    </AlertDialogDescription>
                </AlertDialogHeader>
                <div aria-live="polite">
                    {error && <Alert variant="destructive"><AlertDescription>{error}</AlertDescription></Alert>}
                </div>
                <AlertDialogFooter>
                    <Button variant="outline" autoFocus disabled={busy} onClick={close}>Отмена</Button>
                    <Button variant="destructive" disabled={busy} data-testid="dataset-operation-deletion-confirm" onClick={() => void commit()}>
                        {busy ? "Удаляем…" : "Удалить навсегда"}
                    </Button>
                </AlertDialogFooter>
            </AlertDialogContent>
        </AlertDialog>
    );
}
