import { useState } from "react";
import { useTranslation } from "react-i18next";
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
 * "Delete the preset for good?" / "Delete the run from the history?". A preset is deleted with its revision, an unfinished run of the
 * preset blocks deletion (a host error); for a run only the record is deleted — the group and the images stay. While deletion goes
 * the dialog does not close; the focus returns to the element that opened it.
 */
export function GenerationDeletionDialog({ projectId, target, close, deleted }: {
    projectId: string;
    target: GenerationDeletionTarget;
    close: () => void;
    deleted: (target: GenerationDeletionTarget) => void;
}) {
    const { t } = useTranslation("generation");
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
            setError(errorMessage(cause, t("deletion.failed")));
        } finally {
            setBusy(false);
        }
    }

    return (
        <AlertDialog open onOpenChange={open => { if (!open && !busy) close(); }}>
            <AlertDialogContent className="max-h-[85vh] overflow-auto" data-testid="generation-deletion-dialog"
                onEscapeKeyDown={event => { if (busy) event.preventDefault(); }} onCloseAutoFocus={returnFocus}>
                <AlertDialogHeader>
                    <AlertDialogTitle>{target.kind === "recipe" ? t("deletion.recipeTitle") : t("deletion.runTitle")}</AlertDialogTitle>
                    <AlertDialogDescription className="break-words">
                        {t(target.kind === "recipe" ? "deletion.recipeText" : "deletion.runText", { name: target.name })}
                    </AlertDialogDescription>
                </AlertDialogHeader>
                <div aria-live="polite">
                    {error && <Alert variant="destructive" data-testid="generation-deletion-error"><AlertDescription>{error}</AlertDescription></Alert>}
                </div>
                <AlertDialogFooter>
                    <Button variant="outline" autoFocus disabled={busy} onClick={close}>{t("form.cancel")}</Button>
                    <Button variant="destructive" disabled={busy} data-testid="generation-deletion-confirm" onClick={() => void commit()}>
                        {busy ? t("deletion.deleting") : t("deletion.confirm")}
                    </Button>
                </AlertDialogFooter>
            </AlertDialogContent>
        </AlertDialog>
    );
}
