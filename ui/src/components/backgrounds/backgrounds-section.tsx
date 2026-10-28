import { useEffect, useState, type FormEvent, type ReactNode } from "react";
import { Archive, ArchiveRestore, EyeOff, Pencil, Trash2, TriangleAlert } from "lucide-react";
import { backgroundsApi } from "@/api/projects/backgrounds/backgrounds-api";
import type { ImportBackgroundsRequestDto } from "@/api/projects/backgrounds/request-dto/import-backgrounds-request-dto";
import type { BackgroundDeletionResultResponseDto } from "@/api/projects/backgrounds/response-dto/background-deletion-response-dto";
import type { BackgroundResponseDto } from "@/api/projects/backgrounds/response-dto/background-response-dto";
import type { BackgroundSetResponseDto } from "@/api/projects/backgrounds/response-dto/background-set-response-dto";
import type { ProjectResponseDto } from "@/api/projects/response-dto/project-response-dto";
import { BridgeError } from "@/api/transport/errors";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { ImageViewer } from "@/components/viewer/image-viewer";
import { backgroundDetails, setSummary, sizeMismatchText } from "@/lib/backgrounds/background-text";
import { errorMessage } from "@/lib/format";
import { useBackgroundBlur } from "@/lib/use-background-blur";
import { useBackgroundGallery } from "@/lib/use-background-gallery";
import { useBackgroundRuns } from "@/lib/use-background-runs";
import { useBackgroundSets } from "@/lib/use-background-sets";
import { selectionLimit, useRangeSelection } from "@/lib/use-range-selection";
import { BackgroundAddPanel, type FileSelection } from "./background-add-panel";
import { BackgroundGallery } from "./background-gallery";
import { BackgroundImageCropDialog } from "./background-image-crop-dialog";
import { BackgroundRunsPanel } from "./background-runs-panel";
import { BackgroundSetDialog, type BackgroundSetValues } from "./background-set-dialog";
import { BackgroundsDeletionDialog, type BackgroundsDeletionTarget } from "./backgrounds-deletion-dialog";

const conflictCodes = /\.(revision_conflict|deletion_revision_conflict)$/;

type Notice = { tone: "info" | "error"; text: string };

/**
 * Раздел «Фоны»: наборы слева, справа содержимое выбранного набора — действия, «Добавить фоны» (три способа и общее
 * размытие), виртуальная галерея и история подготовки этого набора. Без опроса: backgrounds.changed, job.changed и
 * project.changed (признак несовпадения размера).
 */
export function BackgroundsSection({ project }: { project: ProjectResponseDto }) {
    const projectId = project.id;
    const sets = useBackgroundSets(projectId);
    const [setId, setSetId] = useState<string>();
    const [showRemoved, setShowRemoved] = useState(false);
    const set = sets.sets.find(item => item.id === setId);
    const filter = showRemoved ? "all" : "active";
    const gallery = useBackgroundGallery(projectId, set?.id, filter);
    const runs = useBackgroundRuns(projectId, set?.id);
    const blur = useBackgroundBlur();
    const [notice, setNotice] = useState<Notice>();
    const items = gallery.items;
    const selection = useRangeSelection(items, `${setId}:${filter}`, {
        limit: `За один раз можно выбрать до ${selectionLimit} фонов.`,
        firstPage: `Выбраны первые ${selectionLimit} фонов: операции выполняются не более чем над ${selectionLimit} за раз.`,
    }, text => setNotice({ tone: "info", text }));
    const selected = selection.selected;
    const [busy, setBusy] = useState(false);
    const [name, setName] = useState("");
    const [addOpen, setAddOpen] = useState(true);
    const [runsOpen, setRunsOpen] = useState(false);
    const [crop, setCrop] = useState<FileSelection>();
    const [viewerId, setViewerId] = useState<string>();
    const [viewerBusy, setViewerBusy] = useState(false);
    const [deletion, setDeletion] = useState<BackgroundsDeletionTarget>();
    const [editSet, setEditSet] = useState<BackgroundSetResponseDto>();
    const [dialogError, setDialogError] = useState<string>();

    const archived = !!set?.archivedAt;
    const selectedItems = items.filter(item => selected.has(item.id));
    const allRemoved = selectedItems.length > 0 && selectedItems.every(item => item.removedAt);

    // A vanished set (deleted elsewhere) closes its content.
    useEffect(() => {
        if (setId && !sets.loading && !set) {
            setSetId(undefined);
            setViewerId(undefined);
            setCrop(undefined);
        }
    }, [setId, set, sets.loading]);

    // Добавление раскрыто у пустого набора; у набора с фонами свёрнуто, чтобы галерея была видна.
    const emptySet = set ? set.itemCount + set.removedCount === 0 : true;
    useEffect(() => {
        setAddOpen(emptySet);
        setRunsOpen(false);
        // Only the choice of set decides the initial state; later counts do not fold the panel under the user.
    }, [setId]);

    function describe(cause: unknown): string {
        const message = errorMessage(cause);
        if (cause instanceof BridgeError && conflictCodes.test(cause.code)) {
            if (setId) void sets.reread(setId);
            void gallery.reload();
            return `${message} Список обновлён.`;
        }
        return message;
    }

    async function mutate(action: () => Promise<void>, report: (message: string) => void = text => setNotice({ tone: "error", text })): Promise<boolean> {
        setBusy(true);
        try {
            await action();
            return true;
        } catch (cause) {
            report(describe(cause));
            return false;
        } finally {
            setBusy(false);
        }
    }

    function selectSet(id: string) {
        if (id === setId) return;
        setSetId(id);
        setViewerId(undefined);
        setNotice(undefined);
    }

    function createSet(event: FormEvent) {
        event.preventDefault();
        const value = name.trim();
        if (!value || busy) return;
        setNotice(undefined);
        void mutate(async () => {
            const created = await backgroundsApi.createSet({ projectId, name: value, description: "" });
            sets.apply(created);
            setName("");
            selectSet(created.id);
        });
    }

    function saveSet(values: BackgroundSetValues) {
        const target = editSet;
        if (!target) return;
        setDialogError(undefined);
        void mutate(async () => {
            sets.apply(await backgroundsApi.updateSet({ projectId, setId: target.id, revision: target.revision, ...values }));
            setEditSet(undefined);
        }, setDialogError);
    }

    function toggleArchive(target: BackgroundSetResponseDto) {
        setNotice(undefined);
        void mutate(async () => {
            sets.apply(await backgroundsApi.setSetArchived({ projectId, setId: target.id, archived: !target.archivedAt, revision: target.revision }));
        });
    }

    /** Подготовка: задача ставится сразу, ход и итог приходят в историю набора событиями job.changed. */
    async function start(request: ImportBackgroundsRequestDto) {
        const scope = runs.scope();
        const job = await backgroundsApi.import(request);
        if (runs.isCurrent(scope)) {
            runs.apply(job);
            setRunsOpen(true);
        }
    }

    function setRemoved(targets: readonly BackgroundResponseDto[], removed: boolean) {
        const changed = targets.filter(item => !!item.removedAt !== removed);
        if (!changed.length || !set) return;
        setNotice(undefined);
        void mutate(async () => {
            const result = await backgroundsApi.setRemoved({ projectId, removed, items: changed.map(item => ({ id: item.id, revision: item.revision })) });
            gallery.applyItems(result);
            if (removed) {
                selection.forget(filter === "active" ? result.map(item => item.id) : []);
                if (viewerId && result.some(item => item.id === viewerId)) setViewerId(undefined);
            }
            void sets.reread(set.id);
        });
    }

    function requestDeletion(itemIds?: readonly string[], label?: string) {
        if (!set) return;
        setNotice(undefined);
        setDeletion({ setId: set.id, name: label ?? set.name, itemIds });
    }

    function deleted(result: BackgroundDeletionResultResponseDto) {
        const target = deletion;
        setDeletion(undefined);
        setNotice(result.cleanupPending
            ? { tone: "info", text: "Материалы удалены из каталога. Очистка файлов продолжится автоматически при следующем запуске приложения." }
            : undefined);
        selection.clear();
        if (result.setDeleted) {
            sets.remove(result.setId);
            setSetId(undefined);
            setViewerId(undefined);
            return;
        }
        if (target?.itemIds) {
            gallery.removeItems(target.itemIds);
            if (viewerId && target.itemIds.includes(viewerId)) setViewerId(undefined);
        }
        void sets.reread(result.setId);
    }

    // The viewer walks the visible (not removed) backgrounds of the loaded pages and loads the next page at the end.
    const visible = items.filter(item => !item.removedAt);
    const viewed = viewerId ? visible.find(item => item.id === viewerId) : undefined;
    const viewerIndex = viewed ? visible.indexOf(viewed) : -1;

    async function viewNeighbour(direction: -1 | 1) {
        const next = visible[viewerIndex + direction];
        if (next || direction < 0 || !gallery.hasMore) {
            if (next) setViewerId(next.id);
            return;
        }
        setViewerBusy(true);
        try {
            const page = await gallery.loadMore();
            const found = page.find(item => !item.removedAt);
            if (found) setViewerId(found.id);
        } finally {
            setViewerBusy(false);
        }
    }

    return (
        <div className="flex min-h-0 flex-1 flex-col gap-3 overflow-hidden" data-testid="backgrounds-section">
            {notice && (
                <Alert variant={notice.tone === "error" ? "destructive" : "default"} className="shrink-0" data-testid="backgrounds-notice">
                    <AlertDescription>
                        <p>{notice.text}</p>
                        <Button variant="ghost" size="sm" className="mt-1" onClick={() => setNotice(undefined)}>Скрыть</Button>
                    </AlertDescription>
                </Alert>
            )}
            <form className="flex max-w-2xl shrink-0 items-end gap-3" onSubmit={createSet}>
                <label className="min-w-0 flex-1 space-y-2">
                    <span className="text-xs text-muted-foreground">Название набора фонов</span>
                    <Input required maxLength={120} aria-label="Название набора фонов" placeholder="Новый набор фонов" data-testid="background-set-new-name"
                        value={name} onChange={event => setName(event.target.value)} />
                </label>
                <Button type="submit" data-testid="background-set-create" disabled={busy || !name.trim()}>Создать набор</Button>
            </form>
            <div className="grid min-h-0 flex-1 grid-cols-[150px_minmax(0,1fr)] gap-5 lg:grid-cols-[220px_minmax(0,1fr)]">
                <section className="min-h-0 overflow-y-auto overscroll-contain rounded-md border p-2" aria-label="Наборы фонов" data-testid="background-sets" tabIndex={0}>
                    <div className="space-y-2">
                        {sets.sets.map(item => (
                            <button
                                key={item.id}
                                type="button"
                                data-background-set-id={item.id}
                                data-archived={item.archivedAt ? "true" : undefined}
                                className={`w-full rounded-md px-3 py-3 text-left outline-none transition-colors focus-visible:ring-2 focus-visible:ring-ring ${item.id === setId ? "bg-accent text-accent-foreground" : "hover:bg-accent/50"}`}
                                aria-pressed={item.id === setId}
                                onClick={() => selectSet(item.id)}
                            >
                                <span className="block text-sm font-medium break-words">{item.name}</span>
                                <span className="mt-1 block text-xs text-muted-foreground" data-testid="background-set-summary">{setSummary(item)}</span>
                                {!item.matchesProjectInput && (
                                    <span className="mt-1 flex items-center gap-1 text-xs text-amber-700 dark:text-amber-400" title={sizeMismatchText(item)} data-testid="set-size-mismatch">
                                        <TriangleAlert className="size-3.5 shrink-0" aria-hidden />Размер отличается от входа
                                    </span>
                                )}
                            </button>
                        ))}
                        {sets.loading && !sets.sets.length && <p role="status" className="p-2 text-sm text-muted-foreground">Загрузка наборов…</p>}
                        {sets.error && (
                            <Alert variant="destructive">
                                <AlertDescription>
                                    <p>{sets.error}</p>
                                    <Button variant="ghost" size="sm" className="mt-1" onClick={() => void sets.refresh()}>Повторить</Button>
                                </AlertDescription>
                            </Alert>
                        )}
                        {!sets.loading && !sets.error && !sets.sets.length && <Empty>Создайте набор для подготовки фонов генерации.</Empty>}
                    </div>
                </section>
                <section className="flex min-h-0 min-w-0 flex-col gap-3 overflow-y-auto overscroll-contain" aria-label="Содержимое набора фонов" data-testid="background-set-content">
                    {set ? (
                        <>
                            <div className="flex shrink-0 flex-col gap-2">
                                <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
                                    <h2 className="min-w-0 font-medium break-words" data-testid="background-set-title">{set.name}</h2>
                                    <span className="text-xs text-muted-foreground tabular-nums" data-testid="background-set-count">
                                        {setSummary(set)}{set.removedCount ? ` · убрано: ${set.removedCount.toLocaleString("ru-RU")}` : ""}
                                    </span>
                                </div>
                                {set.description && <p className="max-w-prose text-xs text-muted-foreground break-words">{set.description}</p>}
                                {!set.matchesProjectInput && (
                                    <p className="flex items-center gap-2 text-xs text-amber-700 dark:text-amber-400" data-testid="set-size-mismatch-note">
                                        <TriangleAlert className="size-4 shrink-0" aria-hidden />
                                        {sizeMismatchText(set)} {project.inputSize.width} × {project.inputSize.height}: такой набор не подойдёт генерации нового размера.
                                    </p>
                                )}
                                <div className="flex flex-wrap items-center gap-2">
                                    <Button size="sm" variant="ghost" aria-label={`Изменить набор фонов: ${set.name}`} disabled={busy} onClick={() => { setDialogError(undefined); setEditSet(set); }}>
                                        <Pencil />Изменить
                                    </Button>
                                    <Button size="sm" variant="ghost" aria-label={`${set.archivedAt ? "Восстановить" : "В архив"}: ${set.name}`} disabled={busy} onClick={() => toggleArchive(set)}>
                                        {set.archivedAt ? <><ArchiveRestore />Восстановить</> : <><Archive />В архив</>}
                                    </Button>
                                    <Button size="sm" variant="ghost" className="text-destructive hover:text-destructive" aria-label={`Удалить набор фонов: ${set.name}`} disabled={busy}
                                        onClick={() => requestDeletion()}>
                                        <Trash2 />Удалить набор
                                    </Button>
                                    {items.length > 0 && (
                                        <Button size="sm" variant="ghost" data-testid="background-select-page" onClick={selection.selectPage}>
                                            {selected.size ? "Снять выделение" : "Выбрать все на странице"}
                                        </Button>
                                    )}
                                    {selected.size > 0 && (
                                        <>
                                            <Button size="sm" variant="outline" disabled={busy || archived} data-testid="background-remove-selected" onClick={() => setRemoved(selectedItems, !allRemoved)}>
                                                {allRemoved ? <><ArchiveRestore />Восстановить ({selected.size})</> : <><EyeOff />Убрать ({selected.size})</>}
                                            </Button>
                                            <Button size="sm" variant="outline" className="text-destructive hover:text-destructive" disabled={busy} data-testid="background-delete-selected"
                                                onClick={() => requestDeletion([...selected], `${set.name} · выбранные фоны`)}>
                                                <Trash2 />Удалить выбранные ({selected.size})
                                            </Button>
                                        </>
                                    )}
                                    <label className="ml-auto flex items-center gap-2 text-xs text-muted-foreground">
                                        <Checkbox checked={showRemoved} data-testid="show-removed" onCheckedChange={value => setShowRemoved(value === true)} />
                                        Показать убранные фоны
                                    </label>
                                </div>
                                {archived && <p className="text-xs text-muted-foreground">Набор в архиве: добавление и «Убрать» недоступны. Восстановите набор, чтобы изменить его.</p>}
                            </div>
                            <BackgroundAddPanel
                                key={set.id}
                                projectId={projectId}
                                set={set}
                                blur={blur}
                                disabled={archived || busy}
                                open={addOpen && !archived}
                                setOpen={setAddOpen}
                                start={start}
                                openCrop={setCrop}
                            />
                            {!items.length && gallery.loading && <p role="status" className="text-sm text-muted-foreground">Загрузка фонов…</p>}
                            {gallery.error && (
                                <Alert variant="destructive" className="shrink-0">
                                    <AlertDescription>
                                        <p>{gallery.error}</p>
                                        <Button variant="ghost" size="sm" className="mt-1" onClick={() => void gallery.reload()}>Повторить</Button>
                                    </AlertDescription>
                                </Alert>
                            )}
                            {!items.length && !gallery.loading && !gallery.error && (
                                <Empty>Фонов пока нет. Добавьте готовые кадры нужного размера или нарежьте их из изображений либо видео.</Empty>
                            )}
                            {items.length > 0 && (
                                <BackgroundGallery
                                    items={items}
                                    selected={selected}
                                    hasMore={gallery.hasMore}
                                    loading={gallery.loading}
                                    busy={busy}
                                    readOnly={archived}
                                    actions={{
                                        toggle: selection.toggle,
                                        view: item => setViewerId(item.id),
                                        setRemoved: (item, removed) => setRemoved([item], removed),
                                        remove: item => requestDeletion([item.id], item.origin.sourceName),
                                        renew: gallery.renew,
                                        loadMore: () => void gallery.loadMore(),
                                    }}
                                />
                            )}
                            <BackgroundRunsPanel runs={runs} open={runsOpen} setOpen={setRunsOpen} />
                        </>
                    ) : (
                        <Empty>
                            {sets.sets.length
                                ? "Выберите набор слева, чтобы добавить или просмотреть фоны."
                                : "Создайте первый набор фонов. Размер кадров должен совпадать с входом модели."}
                        </Empty>
                    )}
                </section>
            </div>
            {viewed && (
                <ImageViewer
                    source={{
                        id: viewed.id,
                        name: viewed.origin.sourceName,
                        width: viewed.width,
                        height: viewed.height,
                        src: viewed.originalUrl,
                        renew: () => gallery.renew(viewed.id),
                        metadata: (
                            <div className="space-y-1" data-testid="background-details">
                                {backgroundDetails(viewed).map(line => <p key={line}>{line}</p>)}
                            </div>
                        ),
                    }}
                    close={() => setViewerId(undefined)}
                    onPrevious={viewerIndex > 0 ? () => void viewNeighbour(-1) : undefined}
                    onNext={viewerIndex < visible.length - 1 || gallery.hasMore ? () => void viewNeighbour(1) : undefined}
                    navigationBusy={viewerBusy}
                    position={`${viewerIndex + 1} / ${visible.length}${gallery.hasMore ? "+" : ""}`}
                />
            )}
            {crop && set && (
                <BackgroundImageCropDialog projectId={projectId} set={set} selection={crop} blur={blur} start={start} close={() => setCrop(undefined)} />
            )}
            {editSet && <BackgroundSetDialog set={editSet} busy={busy} error={dialogError} close={() => setEditSet(undefined)} save={saveSet} />}
            {deletion && <BackgroundsDeletionDialog projectId={projectId} target={deletion} close={() => setDeletion(undefined)} deleted={deleted} />}
        </div>
    );
}

function Empty({ children }: { children: ReactNode }) {
    return (
        <div className="flex min-h-40 flex-col items-center justify-center rounded-md border border-dashed p-6 text-center text-sm text-muted-foreground" data-testid="backgrounds-empty">
            {children}
        </div>
    );
}
