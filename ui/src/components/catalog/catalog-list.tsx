import type { ReactNode } from "react";
import { Archive, ArchiveRestore, Pencil, Plus, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { cn } from "@/lib/utils";

/** Названный объект списка каталога: вариант проекта или класс. */
export interface CatalogEntry {
    id: string;
    name: string;
    description: string;
    archivedAt: string | null;
}

/** Подстрока без учёта регистра; архивные — только при «Показать архив». */
export function filterEntries<T extends CatalogEntry>(items: readonly T[], search: string, showArchive: boolean): T[] {
    const query = search.trim().toLocaleLowerCase("ru");
    return items.filter(item => (showArchive || !item.archivedAt) && item.name.toLocaleLowerCase("ru").includes(query));
}

export function CatalogToolbar({ searchLabel, search, setSearch, showArchive, setShowArchive, addLabel, addDisabled, add }: {
    searchLabel: string;
    search: string;
    setSearch: (value: string) => void;
    showArchive: boolean;
    setShowArchive: (value: boolean) => void;
    addLabel: string;
    addDisabled: boolean;
    add: () => void;
}) {
    return (
        <div className="flex shrink-0 flex-wrap items-center justify-between gap-3">
            <Input
                type="search"
                className="max-w-sm"
                data-testid="catalog-search"
                aria-label={searchLabel}
                placeholder={searchLabel}
                value={search}
                onChange={event => setSearch(event.target.value)}
            />
            <div className="flex items-center gap-4">
                <label className="flex cursor-pointer items-center gap-2 text-sm">
                    <Checkbox data-testid="catalog-show-archive" checked={showArchive} onCheckedChange={value => setShowArchive(value === true)} />
                    Показать архив
                </label>
                <Button data-testid="catalog-add" disabled={addDisabled} onClick={add}>
                    <Plus />{addLabel}
                </Button>
            </div>
        </div>
    );
}

/** Строка списка: название с необязательным префиксом (индекс класса), описание или «В архиве», действия. */
export function CatalogRow({ entry, kindLabel, selected, prefix, fallback, busy, open, openDisabled, edit, toggleArchive, remove, extra, ...data }: {
    entry: CatalogEntry;
    kindLabel: string;
    selected?: boolean;
    prefix?: ReactNode;
    fallback: string;
    busy: boolean;
    open: () => void;
    openDisabled?: boolean;
    edit: () => void;
    toggleArchive: () => void;
    remove: () => void;
    extra?: ReactNode;
    "data-class-id"?: string;
    "data-variant-id"?: string;
}) {
    const archived = !!entry.archivedAt;
    return (
        <div
            {...data}
            data-archived={archived || undefined}
            aria-current={selected || undefined}
            className={cn("flex flex-wrap items-center justify-end gap-1 p-3", selected && "bg-secondary")}
        >
            <button
                type="button"
                disabled={busy || openDisabled}
                className="min-w-0 flex-1 basis-full rounded text-left outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:cursor-default xl:basis-0"
                onClick={open}
            >
                <span className="block font-medium break-words">{prefix}{entry.name}</span>
                <span className="mt-1 block text-xs break-words text-muted-foreground">
                    {archived ? "В архиве" : entry.description || fallback}
                </span>
            </button>
            {extra}
            <Button size="icon-sm" variant="ghost" aria-label={`Изменить ${entry.name}`} title="Изменить" disabled={busy || archived} onClick={edit}>
                <Pencil />
            </Button>
            <Button
                size="icon-sm"
                variant="ghost"
                aria-label={`${archived ? "Восстановить" : "В архив"}: ${entry.name}`}
                title={archived ? "Восстановить" : "В архив"}
                disabled={busy}
                onClick={toggleArchive}
            >
                {archived ? <ArchiveRestore /> : <Archive />}
            </Button>
            <Button
                size="icon-sm"
                variant="ghost"
                className="text-destructive hover:text-destructive"
                aria-label={`Удалить ${kindLabel}: ${entry.name}`}
                title="Удалить"
                disabled={busy}
                onClick={remove}
            >
                <Trash2 />
            </Button>
        </div>
    );
}

export function CatalogEmpty({ searching, title, text }: { searching: boolean; title: string; text: string }) {
    return (
        <div className="flex min-h-48 flex-col justify-center gap-2 p-5 text-center">
            <p className="font-medium">{searching ? "Ничего не найдено" : title}</p>
            <p className="text-sm text-muted-foreground">{searching ? "Измените поиск или включите показ архива." : text}</p>
        </div>
    );
}
