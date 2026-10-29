import { useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import type { BackgroundSetResponseDto } from "@/api/projects/backgrounds/response-dto/background-set-response-dto";
import { DialogError, GuardedDialog } from "@/components/catalog/catalog-dialogs";
import { Button } from "@/components/ui/button";
import { DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import "./locale";

export interface BackgroundSetValues {
    name: string;
    description: string;
}

/** Editing a background set: the name (1–120) and the description (up to 4000). The set size does not change. */
export function BackgroundSetDialog({ set, busy, error, close, save }: {
    set: BackgroundSetResponseDto;
    busy: boolean;
    error?: string;
    close: () => void;
    save: (values: BackgroundSetValues) => void;
}) {
    const { t } = useTranslation("backgrounds");
    const [name, setName] = useState(set.name);
    const [description, setDescription] = useState(set.description);

    function submit(event: FormEvent) {
        event.preventDefault();
        if (!busy && name.trim()) save({ name: name.trim(), description: description.trim() });
    }

    return (
        <GuardedDialog busy={busy} close={close} testId="background-set-dialog">
            <DialogHeader>
                <DialogTitle>{t("setDialog.title")}</DialogTitle>
                <DialogDescription>{t("setDialog.description", { width: set.targetSize.width, height: set.targetSize.height })}</DialogDescription>
            </DialogHeader>
            <form className="space-y-4" onSubmit={submit}>
                <div className="space-y-2">
                    <Label htmlFor="background-set-name">{t("setDialog.name")}</Label>
                    <Input id="background-set-name" data-testid="background-set-name" autoFocus required maxLength={120} value={name} onChange={event => setName(event.target.value)} />
                </div>
                <div className="space-y-2">
                    <Label htmlFor="background-set-description">{t("setDialog.descriptionLabel")}</Label>
                    <Textarea id="background-set-description" data-testid="background-set-description" className="h-24 resize-none" maxLength={4000}
                        value={description} onChange={event => setDescription(event.target.value)} />
                </div>
                <DialogError error={error} />
                <DialogFooter>
                    <Button type="button" variant="outline" disabled={busy} onClick={close}>{t("dialog.cancel")}</Button>
                    <Button type="submit" data-testid="background-set-save" disabled={busy || !name.trim()}>{busy ? t("dialog.saving") : t("dialog.save")}</Button>
                </DialogFooter>
            </form>
        </GuardedDialog>
    );
}
