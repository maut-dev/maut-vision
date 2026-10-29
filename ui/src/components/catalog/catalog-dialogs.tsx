import { useState, type FormEvent, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import type { VariantResponseDto } from "@/api/projects/catalog/response-dto/variant-response-dto";
import { Alert, AlertDescription } from "@/components/ui/alert";
import {
    AlertDialog,
    AlertDialogCancel,
    AlertDialogContent,
    AlertDialogDescription,
    AlertDialogFooter,
    AlertDialogHeader,
    AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Textarea } from "@/components/ui/textarea";
import "@/i18n/sections/catalog";

/**
 * Returns focus to the element that opened the dialog. Radix returns focus only to its own Trigger, while catalog dialogs open from
 * list rows; if the element is gone (for example, the row was archived), focus stays with the document.
 */
export function useReturnFocus() {
    const [opener] = useState(() => document.activeElement instanceof HTMLElement ? document.activeElement : null);
    return (event: Event) => {
        event.preventDefault();
        if (opener?.isConnected) opener.focus();
    };
}

/** A dialog that cannot be closed with Esc, a click outside or the cross while saving. */
export function GuardedDialog({ busy, close, testId, children }: { busy: boolean; close: () => void; testId: string; children: ReactNode }) {
    const returnFocus = useReturnFocus();
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
                {children}
            </DialogContent>
        </Dialog>
    );
}

export function DialogError({ error }: { error?: string }) {
    return (
        <div className="min-h-12" aria-live="polite">
            {error && <Alert variant="destructive" data-testid="catalog-dialog-error"><AlertDescription>{error}</AlertDescription></Alert>}
        </div>
    );
}

export interface EntryValues {
    name: string;
    description: string;
}

/** Creating and editing a project variant or a class: the name (1–120) and the description (up to 2000). */
export function CatalogEntryDialog({ title, description, initial, busy, error, close, save }: {
    title: string;
    description: string;
    initial?: EntryValues;
    busy: boolean;
    error?: string;
    close: () => void;
    save: (values: EntryValues) => void;
}) {
    const { t } = useTranslation("catalog");
    const [name, setName] = useState(initial?.name ?? "");
    const [text, setText] = useState(initial?.description ?? "");

    function submit(event: FormEvent) {
        event.preventDefault();
        if (busy || !name.trim()) return;
        save({ name: name.trim(), description: text.trim() });
    }

    return (
        <GuardedDialog busy={busy} close={close} testId="catalog-entry-dialog">
            <DialogHeader>
                <DialogTitle>{title}</DialogTitle>
                <DialogDescription>{description}</DialogDescription>
            </DialogHeader>
            <form className="space-y-4" onSubmit={submit}>
                <div className="space-y-2">
                    <Label htmlFor="catalog-name">{t("dialog.name")}</Label>
                    <Input id="catalog-name" data-testid="catalog-name" autoFocus required maxLength={120} value={name} onChange={event => setName(event.target.value)} />
                </div>
                <div className="space-y-2">
                    <Label htmlFor="catalog-description">{t("dialog.description")}</Label>
                    <Textarea id="catalog-description" className="h-24 resize-none" maxLength={2000} value={text} onChange={event => setText(event.target.value)} />
                </div>
                <DialogError error={error} />
                <DialogFooter>
                    <Button type="button" variant="outline" disabled={busy} onClick={close}>{t("dialog.cancel")}</Button>
                    <Button type="submit" data-testid="catalog-save" disabled={busy || !name.trim()}>{busy ? t("dialog.saving") : t("dialog.save")}</Button>
                </DialogFooter>
            </form>
        </GuardedDialog>
    );
}

/** Attaching an active project variant that the class does not have yet. */
export function AttachVariantDialog({ variants, busy, error, close, save }: {
    variants: readonly VariantResponseDto[];
    busy: boolean;
    error?: string;
    close: () => void;
    save: (variantId: string) => void;
}) {
    const { t } = useTranslation("catalog");
    const [variantId, setVariantId] = useState(variants.length === 1 ? variants[0].id : "");

    function submit(event: FormEvent) {
        event.preventDefault();
        if (!busy && variantId) save(variantId);
    }

    return (
        <GuardedDialog busy={busy} close={close} testId="attach-variant-dialog">
            <DialogHeader>
                <DialogTitle>{t("dialog.attachTitle")}</DialogTitle>
                <DialogDescription>{t("dialog.attachDescription")}</DialogDescription>
            </DialogHeader>
            <form className="space-y-4" onSubmit={submit}>
                <div className="space-y-2">
                    <Label htmlFor="attach-variant">{t("dialog.attachLabel")}</Label>
                    <Select value={variantId} onValueChange={setVariantId} disabled={busy}>
                        <SelectTrigger id="attach-variant" data-testid="attach-variant" className="w-full" autoFocus>
                            <SelectValue placeholder={t("dialog.chooseVariant")} />
                        </SelectTrigger>
                        <SelectContent>
                            {variants.map(item => <SelectItem key={item.id} value={item.id}>{item.name}</SelectItem>)}
                        </SelectContent>
                    </Select>
                    <p className="text-xs text-muted-foreground">{t("dialog.attachHint")}</p>
                </div>
                <DialogError error={error} />
                <DialogFooter>
                    <Button type="button" variant="outline" disabled={busy} onClick={close}>{t("dialog.cancel")}</Button>
                    <Button type="submit" data-testid="attach-save" disabled={busy || !variantId}>{busy ? t("dialog.saving") : t("dialog.attach")}</Button>
                </DialogFooter>
            </form>
        </GuardedDialog>
    );
}

/** Confirming archiving; restoring needs no confirmation. */
export function ArchiveConfirmDialog({ name, busy, error, close, confirm }: {
    name: string;
    busy: boolean;
    error?: string;
    close: () => void;
    confirm: () => void;
}) {
    const { t } = useTranslation("catalog");
    const returnFocus = useReturnFocus();
    return (
        <AlertDialog open onOpenChange={open => { if (!open && !busy) close(); }}>
            <AlertDialogContent
                data-testid="archive-confirm"
                onEscapeKeyDown={event => { if (busy) event.preventDefault(); }}
                onCloseAutoFocus={returnFocus}
            >
                <AlertDialogHeader>
                    <AlertDialogTitle>{t("dialog.archiveTitle")}</AlertDialogTitle>
                    <AlertDialogDescription className="break-words">
                        {t("dialog.archiveText", { name })}
                    </AlertDialogDescription>
                </AlertDialogHeader>
                <div className="min-h-6" aria-live="polite">
                    {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
                </div>
                <AlertDialogFooter>
                    <AlertDialogCancel disabled={busy}>{t("dialog.cancel")}</AlertDialogCancel>
                    <Button data-testid="archive-confirm-action" disabled={busy} onClick={confirm}>{busy ? t("dialog.saving") : t("dialog.archive")}</Button>
                </AlertDialogFooter>
            </AlertDialogContent>
        </AlertDialog>
    );
}
