import { useState } from "react";
import { generationApi } from "@/api/projects/generation/generation-api";
import { useReturnFocus } from "@/components/catalog/catalog-dialogs";
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
import { errorMessage } from "@/lib/format";

export type GenerationDeletionTarget =
    | { kind: "recipe"; id: string; name: string; revision: number }
    | { kind: "run"; id: string; name: string };

/**
 * «Удалить пресет навсегда?» / «Удалить запуск из истории?». Пресет удаляется с ревизией,
 * незавершённый запуск пресета блокирует удаление (ошибка host); у запуска удаляется только запись — группа и изображения
 * остаются. Пока идёт удаление, окно не закрывается; фокус возвращается к открывшему элементу.
 */
export function GenerationDeletionDialog({ projectId, target, close, deleted }: {
    projectId: string;
    target: GenerationDeletionTarget;
    close: () => void;
    deleted: (target: GenerationDeletionTarget) => void;
}) {
    const [busy, setBusy] = useState(false);
    const [error, setError] = useState<string>();
    const returnFocus = useReturnFocus();

    async function commit() {
        if (busy) return;
        setBusy(true);
        setError(undefined);
        try {
            if (target.kind === "recipe") await generationApi.deleteRecipe({ projectId, recipeId: target.id, revision: target.revision });
            else await generationApi.deleteRun({ projectId, runId: target.id });
            deleted(target);
        } catch (cause) {
            setError(errorMessage(cause, "Не удалось удалить. Обновите список и повторите действие."));
        } finally {
            setBusy(false);
        }
    }

    return (
        <AlertDialog open onOpenChange={open => { if (!open && !busy) close(); }}>
            <AlertDialogContent className="max-h-[85vh] overflow-auto" data-testid="generation-deletion-dialog"
                onEscapeKeyDown={event => { if (busy) event.preventDefault(); }} onCloseAutoFocus={returnFocus}>
                <AlertDialogHeader>
                    <AlertDialogTitle>{target.kind === "recipe" ? "Удалить пресет навсегда?" : "Удалить запуск из истории?"}</AlertDialogTitle>
                    <AlertDialogDescription className="break-words">
                        {target.name}. {target.kind === "recipe"
                            ? "Настройки пресета будут удалены. История запусков, их сохранённые настройки и изображения сохранятся."
                            : "Запись запуска и его настройки будут удалены. Созданная группа, изображения и разметка сохранятся."} Восстановить запись в приложении нельзя.
                    </AlertDialogDescription>
                </AlertDialogHeader>
                <div aria-live="polite">
                    {error && <Alert variant="destructive" data-testid="generation-deletion-error"><AlertDescription>{error}</AlertDescription></Alert>}
                </div>
                <AlertDialogFooter>
                    <Button variant="outline" autoFocus disabled={busy} onClick={close}>Отмена</Button>
                    <Button variant="destructive" disabled={busy} data-testid="generation-deletion-confirm" onClick={() => void commit()}>
                        {busy ? "Удаляем…" : "Удалить навсегда"}
                    </Button>
                </AlertDialogFooter>
            </AlertDialogContent>
        </AlertDialog>
    );
}
