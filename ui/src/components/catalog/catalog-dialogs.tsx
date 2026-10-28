import { useState, type FormEvent, type ReactNode } from "react";
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

/**
 * Возврат фокуса к элементу, открывшему диалог. Radix возвращает фокус только своему Trigger, а диалоги каталога
 * открываются из строк списка; если элемент исчез (например, строка ушла в архив), фокус остаётся у документа.
 */
export function useReturnFocus() {
    const [opener] = useState(() => document.activeElement instanceof HTMLElement ? document.activeElement : null);
    return (event: Event) => {
        event.preventDefault();
        if (opener?.isConnected) opener.focus();
    };
}

/** Диалог, который нельзя закрыть Esc, щелчком вне окна или крестиком, пока идёт сохранение. */
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

/** Создание и изменение варианта проекта или класса: название (1–120) и описание (до 2000). */
export function CatalogEntryDialog({ title, description, initial, busy, error, close, save }: {
    title: string;
    description: string;
    initial?: EntryValues;
    busy: boolean;
    error?: string;
    close: () => void;
    save: (values: EntryValues) => void;
}) {
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
                    <Label htmlFor="catalog-name">Название</Label>
                    <Input id="catalog-name" data-testid="catalog-name" autoFocus required maxLength={120} value={name} onChange={event => setName(event.target.value)} />
                </div>
                <div className="space-y-2">
                    <Label htmlFor="catalog-description">Описание</Label>
                    <Textarea id="catalog-description" className="h-24 resize-none" maxLength={2000} value={text} onChange={event => setText(event.target.value)} />
                </div>
                <DialogError error={error} />
                <DialogFooter>
                    <Button type="button" variant="outline" disabled={busy} onClick={close}>Отмена</Button>
                    <Button type="submit" data-testid="catalog-save" disabled={busy || !name.trim()}>{busy ? "Сохранение…" : "Сохранить"}</Button>
                </DialogFooter>
            </form>
        </GuardedDialog>
    );
}

/** Подключение активного варианта проекта, ещё не добавленного к классу. */
export function AttachVariantDialog({ variants, busy, error, close, save }: {
    variants: readonly VariantResponseDto[];
    busy: boolean;
    error?: string;
    close: () => void;
    save: (variantId: string) => void;
}) {
    const [variantId, setVariantId] = useState(variants.length === 1 ? variants[0].id : "");

    function submit(event: FormEvent) {
        event.preventDefault();
        if (!busy && variantId) save(variantId);
    }

    return (
        <GuardedDialog busy={busy} close={close} testId="attach-variant-dialog">
            <DialogHeader>
                <DialogTitle>Подключить вариант</DialogTitle>
                <DialogDescription>Выберите вариант из общего справочника проекта.</DialogDescription>
            </DialogHeader>
            <form className="space-y-4" onSubmit={submit}>
                <div className="space-y-2">
                    <Label htmlFor="attach-variant">Вариант проекта</Label>
                    <Select value={variantId} onValueChange={setVariantId} disabled={busy}>
                        <SelectTrigger id="attach-variant" data-testid="attach-variant" className="w-full" autoFocus>
                            <SelectValue placeholder="Выберите вариант" />
                        </SelectTrigger>
                        <SelectContent>
                            {variants.map(item => <SelectItem key={item.id} value={item.id}>{item.name}</SelectItem>)}
                        </SelectContent>
                    </Select>
                    <p className="text-xs text-muted-foreground">Новые варианты создаются в разделе «Варианты».</p>
                </div>
                <DialogError error={error} />
                <DialogFooter>
                    <Button type="button" variant="outline" disabled={busy} onClick={close}>Отмена</Button>
                    <Button type="submit" data-testid="attach-save" disabled={busy || !variantId}>{busy ? "Сохранение…" : "Подключить"}</Button>
                </DialogFooter>
            </form>
        </GuardedDialog>
    );
}

/** Подтверждение переноса в архив; восстановление подтверждения не требует. */
export function ArchiveConfirmDialog({ name, busy, error, close, confirm }: {
    name: string;
    busy: boolean;
    error?: string;
    close: () => void;
    confirm: () => void;
}) {
    const returnFocus = useReturnFocus();
    return (
        <AlertDialog open onOpenChange={open => { if (!open && !busy) close(); }}>
            <AlertDialogContent
                data-testid="archive-confirm"
                onEscapeKeyDown={event => { if (busy) event.preventDefault(); }}
                onCloseAutoFocus={returnFocus}
            >
                <AlertDialogHeader>
                    <AlertDialogTitle>Перенести в архив?</AlertDialogTitle>
                    <AlertDialogDescription className="break-words">
                        «{name}» будет скрыт из рабочих списков. Существующие датасеты сохранят материалы. Архив можно восстановить.
                    </AlertDialogDescription>
                </AlertDialogHeader>
                <div className="min-h-6" aria-live="polite">
                    {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
                </div>
                <AlertDialogFooter>
                    <AlertDialogCancel disabled={busy}>Отмена</AlertDialogCancel>
                    <Button data-testid="archive-confirm-action" disabled={busy} onClick={confirm}>{busy ? "Сохранение…" : "В архив"}</Button>
                </AlertDialogFooter>
            </AlertDialogContent>
        </AlertDialog>
    );
}
