import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent, type ReactNode } from "react";
import { Archive, ArchiveRestore, Film, FolderOpen, MoveRight, Pencil, Trash2, Upload } from "lucide-react";
import { classesApi } from "@/api/projects/catalog/classes-api";
import type { ClassVariantResponseDto } from "@/api/projects/catalog/response-dto/class-variant-response-dto";
import { imagesApi } from "@/api/projects/images/images-api";
import type { ImageArchiveFilter } from "@/api/projects/images/request-dto/list-images-request-dto";
import type { SavedImageAnnotationResponseDto } from "@/api/projects/images/response-dto/image-annotation-response-dto";
import type { ImageDeletionResultResponseDto } from "@/api/projects/images/response-dto/image-deletion-response-dto";
import type { ImageGroupResponseDto } from "@/api/projects/images/response-dto/image-group-response-dto";
import type { ImageResponseDto, ImageReviewState } from "@/api/projects/images/response-dto/image-response-dto";
import type { ProjectResponseDto } from "@/api/projects/response-dto/project-response-dto";
import { BridgeError } from "@/api/transport/errors";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { useNavigationGuard } from "@/components/unsaved-changes";
import { ImageViewer } from "@/components/viewer/image-viewer";
import { draftOf, type AnnotationDraft } from "@/lib/annotation/draft";
import { errorMessage } from "@/lib/format";
import { useCatalog } from "@/lib/use-catalog";
import { useImageGallery, type GalleryFilter } from "@/lib/use-image-gallery";
import { useImageGroups } from "@/lib/use-image-groups";
import { useImageImports } from "@/lib/use-image-imports";
import { selectionLimit, useRangeSelection } from "@/lib/use-range-selection";
import { AnnotationEditor } from "./annotation-editor";
import { createPalette } from "./annotation-palette";
import { annotationOverlays, ImageDetails } from "./image-details";
import { ImageGallery } from "./image-gallery";
import { ImageGroupDialog, MoveImagesDialog, splitHintLabels, type GroupValues } from "./image-group-dialogs";
import { ImageImportsPanel } from "./image-imports-panel";
import { ImagesDeletionDialog, type ImagesDeletionTarget } from "./images-deletion-dialog";
import { VideoFrameImport } from "./video-frame-import";

const generationLock = "Генерация наполняет эту группу. Редактирование станет доступно после завершения.";
const conflictCodes = /\.(revision_conflict|deletion_revision_conflict)$/;

type Notice = { tone: "info" | "error"; text: string };

interface ViewerState {
    imageId: string;
    /** Черновик из редактора; без него показывается сохранённая разметка. */
    draft?: AnnotationDraft;
    revision?: number;
    fromEditor: boolean;
}

const reviewFilterValue = (value: ImageReviewState | null) => value ?? "any";

/**
 * Раздел «Изображения»: группы слева, справа галерея выбранной группы или редактор разметки. Все переходы, которые
 * могут потерять несохранённую разметку, проходят через общую защиту (useNavigationGuard). initialGroupId — группа,
 * выбранная при открытии раздела (например, «Открыть изображения» запуска генерации).
 */
export function ImagesSection({ project, initialGroupId }: { project: ProjectResponseDto; initialGroupId?: string }) {
    const projectId = project.id;
    const groups = useImageGroups(projectId);
    const catalog = useCatalog(projectId);
    const palette = useMemo(() => createPalette(catalog.workspace), [catalog.workspace]);
    const guard = useNavigationGuard();
    const [groupId, setGroupId] = useState<string | undefined>(initialGroupId);
    const [filter, setFilter] = useState<GalleryFilter>({ archive: "active", reviewState: null });
    const group = groups.groups.find(item => item.id === groupId);
    const gallery = useImageGallery(projectId, group?.id, filter);
    const imports = useImageImports(projectId, group?.id);
    const [editing, setEditing] = useState<ImageResponseDto>();
    const [viewer, setViewer] = useState<ViewerState>();
    const [viewerBusy, setViewerBusy] = useState(false);
    const [overlays, setOverlays] = useState({ imageId: "", objects: true, roi: true });
    const [viewerAnnotation, setViewerAnnotation] = useState<{ imageId: string; draft: AnnotationDraft; revision: number }>();
    const [busy, setBusy] = useState(false);
    const [notice, setNotice] = useState<Notice>();
    const [name, setName] = useState("");
    const [editGroup, setEditGroup] = useState<ImageGroupResponseDto>();
    const [dialogError, setDialogError] = useState<string>();
    const [moving, setMoving] = useState(false);
    const [deletion, setDeletion] = useState<ImagesDeletionTarget>();
    const [skipDuplicates, setSkipDuplicates] = useState(true);
    const [recursive, setRecursive] = useState(false);
    const [videoOpen, setVideoOpen] = useState(false);
    const returnTo = useRef<string | undefined>(undefined);

    const items = gallery.items;
    const selection = useRangeSelection(items, `${groupId}:${filter.archive}:${filter.reviewState}`, {
        limit: `За один раз можно выбрать до ${selectionLimit} изображений.`,
        firstPage: `Выбраны первые ${selectionLimit} изображений: операции выполняются не более чем над ${selectionLimit} за раз.`,
    }, text => setNotice({ tone: "info", text }));
    const selected = selection.selected;
    const editImage = editing ? items.find(item => item.id === editing.id) ?? editing : undefined;
    const generating = group?.generationState === "producing";
    const importLocked = !group || generating || !!group.archivedAt || group.origin !== "manual";
    const selectedImages = items.filter(item => selected.has(item.id));
    const activeCount = group ? group.imageCount - group.archivedImageCount : 0;

    // A vanished group (deleted elsewhere) closes its screen.
    useEffect(() => {
        if (groupId && !groups.loading && !group) {
            setGroupId(undefined);
            setEditing(undefined);
            setViewer(undefined);
        }
    }, [groupId, group, groups.loading]);

    // A group chosen on entry is scrolled into view and focused once the list shows it.
    const revealed = useRef(!initialGroupId);
    useEffect(() => {
        if (revealed.current || !group) return;
        revealed.current = true;
        requestAnimationFrame(() => {
            const button = document.querySelector<HTMLElement>(`[data-image-group-id="${group.id}"]`);
            button?.scrollIntoView({ block: "nearest" });
            button?.focus();
        });
    }, [group]);

    useEffect(() => {
        // Focus returns to the tile that opened the editor; the virtual grid may have recreated it.
        const id = returnTo.current;
        if (editing || !id) return;
        returnTo.current = undefined;
        requestAnimationFrame(() => document.querySelector<HTMLElement>(`[data-image-id="${id}"] [data-testid="image-open"]`)?.focus());
    }, [editing]);

    function describe(cause: unknown): string {
        const message = errorMessage(cause);
        if (cause instanceof BridgeError && conflictCodes.test(cause.code)) {
            if (groupId) void groups.reread(groupId);
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

    function selectGroup(id: string) {
        if (id === groupId && !editing) return;
        guard(() => {
            setEditing(undefined);
            setViewer(undefined);
            setGroupId(id);
            setNotice(undefined);
        });
    }

    function changeFilter(next: GalleryFilter) {
        guard(() => {
            setEditing(undefined);
            setFilter(next);
        });
    }

    function createGroup(event: FormEvent) {
        event.preventDefault();
        const value = name.trim();
        if (!value || busy) return;
        setNotice(undefined);
        void mutate(async () => {
            const created = await imagesApi.createGroup({ projectId, name: value, description: "", splitHint: "unassigned" });
            groups.apply(created);
            setName("");
            selectGroup(created.id);
        });
    }

    function saveGroup(values: GroupValues) {
        const target = editGroup;
        if (!target) return;
        setDialogError(undefined);
        void mutate(async () => {
            groups.apply(await imagesApi.updateGroup({ projectId, groupId: target.id, revision: target.revision, ...values }));
            setEditGroup(undefined);
        }, setDialogError);
    }

    function toggleGroupArchive(target: ImageGroupResponseDto) {
        setNotice(undefined);
        guard(() => void mutate(async () => {
            if (!target.archivedAt) setEditing(undefined);
            groups.apply(await imagesApi.setGroupArchived({ projectId, groupId: target.id, archived: !target.archivedAt, revision: target.revision }));
        }));
    }

    function archiveSelected(archived: boolean) {
        const targets = selectedImages.filter(item => !!item.archivedAt !== archived);
        if (!targets.length) return;
        setNotice(undefined);
        guard(() => void mutate(async () => {
            const changed = await imagesApi.setArchived({ projectId, archived, images: targets.map(item => ({ id: item.id, revision: item.revision })) });
            gallery.applyImages(changed);
            selection.clear();
            if (groupId) void groups.reread(groupId);
        }));
    }

    function moveSelected(target: ImageGroupResponseDto) {
        if (!group) return;
        setDialogError(undefined);
        void mutate(async () => {
            const result = await imagesApi.move({
                projectId,
                sourceGroupId: group.id,
                sourceRevision: group.revision,
                targetGroupId: target.id,
                targetRevision: target.revision,
                images: selectedImages.map(item => ({ id: item.id, revision: item.revision })),
            });
            groups.apply(result.source);
            groups.apply(result.target);
            gallery.removeImages(result.images.map(item => item.id));
            selection.clear();
            setMoving(false);
            setNotice({ tone: "info", text: `Перемещено в «${target.name}»: ${result.images.length.toLocaleString("ru-RU")}.` });
        }, setDialogError);
    }

    function requestDeletion(imageIds?: readonly string[]) {
        if (!group) return;
        setNotice(undefined);
        guard(() => setDeletion({ groupId: group.id, name: group.name, imageIds }));
    }

    function deleted(result: ImageDeletionResultResponseDto) {
        const target = deletion;
        setDeletion(undefined);
        setNotice(result.cleanupPending ? { tone: "info", text: "Удаление завершено. Занятые файлы будут очищены в фоне." } : undefined);
        selection.clear();
        if (result.groupDeleted) {
            groups.remove(result.groupId);
            setGroupId(undefined);
            setEditing(undefined);
            setViewer(undefined);
            return;
        }
        if (target?.imageIds) {
            gallery.removeImages(target.imageIds);
            if (editing && target.imageIds.includes(editing.id)) setEditing(undefined);
        }
        void groups.reread(result.groupId);
    }

    const openEditor = useCallback((image: ImageResponseDto) => {
        guard(() => {
            returnTo.current = image.id;
            setViewer(undefined);
            setEditing(image);
        });
    }, [guard]);

    /** Сосед в порядке галереи; после последнего загруженного — первая картинка следующей страницы. */
    async function neighbour(imageId: string, direction: -1 | 1): Promise<ImageResponseDto | undefined> {
        const index = items.findIndex(item => item.id === imageId);
        const next = items[index + direction];
        if (next || direction < 0 || !gallery.hasMore) return next;
        setViewerBusy(true);
        try {
            return (await gallery.loadMore())[0];
        } finally {
            setViewerBusy(false);
        }
    }

    const editorIndex = editImage ? items.findIndex(item => item.id === editImage.id) : -1;
    const editorNavigation = editImage && editorIndex >= 0 ? {
        previous: editorIndex > 0 ? () => openEditor(items[editorIndex - 1]) : undefined,
        next: editorIndex < items.length - 1 || gallery.hasMore
            ? () => guard(() => void neighbour(editImage.id, 1).then(image => { if (image) { returnTo.current = image.id; setEditing(image); } }))
            : undefined,
    } : {};

    function saved(result: SavedImageAnnotationResponseDto) {
        gallery.applyImages([result.image]);
        gallery.applyAnnotation(result.annotation);
        setEditing(current => current?.id === result.image.id ? result.image : current);
    }

    function setColor(binding: ClassVariantResponseDto, color: string | null) {
        classesApi.setVariantColor({ projectId, classVariantId: binding.id, revision: binding.revision, color })
            .then(catalog.applyClassVariant, cause => setNotice({ tone: "error", text: describe(cause) }));
    }

    // The viewer opened from the gallery shows the saved annotation of the page, or reads it.
    const viewed = viewer ? items.find(item => item.id === viewer.imageId) ?? (editImage?.id === viewer.imageId ? editImage : undefined) : undefined;
    useEffect(() => {
        if (!viewer || viewer.draft) return;
        const page = gallery.annotations[viewer.imageId];
        if (page) {
            setViewerAnnotation({ imageId: viewer.imageId, draft: draftOf(page), revision: page.revision });
            return;
        }
        const controller = new AbortController();
        imagesApi.getAnnotation({ projectId, imageId: viewer.imageId }, controller.signal).then(
            value => setViewerAnnotation({ imageId: value.imageId, draft: draftOf(value), revision: value.revision }),
            () => {});
        return () => controller.abort();
    }, [viewer, gallery.annotations, projectId]);

    function view(image: ImageResponseDto) {
        setViewer({ imageId: image.id, fromEditor: false });
    }

    async function viewNeighbour(direction: -1 | 1) {
        if (!viewer) return;
        const image = await neighbour(viewer.imageId, direction);
        if (image) setViewer({ imageId: image.id, fromEditor: false });
    }

    const shownAnnotation = viewer?.draft ?? (viewerAnnotation?.imageId === viewer?.imageId ? viewerAnnotation?.draft : undefined);
    const shownRevision = viewer?.draft ? viewer.revision : viewerAnnotation?.imageId === viewer?.imageId ? viewerAnnotation?.revision : undefined;
    const showObjects = overlays.imageId !== viewer?.imageId || overlays.objects;
    const showRoi = overlays.imageId !== viewer?.imageId || overlays.roi;
    const viewerIndex = viewed ? items.findIndex(item => item.id === viewed.id) : -1;
    const moveTargets = groups.groups.filter(item => item.id !== group?.id && item.origin === group?.origin && !item.archivedAt && item.generationState !== "producing");
    const allArchived = selectedImages.length > 0 && selectedImages.every(item => item.archivedAt);

    return (
        <div className="flex min-h-0 flex-1 flex-col gap-3 overflow-hidden" data-testid="images-section">
            {notice && (
                <Alert variant={notice.tone === "error" ? "destructive" : "default"} className="shrink-0" data-testid="images-notice">
                    <AlertDescription>
                        <p>{notice.text}</p>
                        <Button variant="ghost" size="sm" className="mt-1" onClick={() => setNotice(undefined)}>Скрыть</Button>
                    </AlertDescription>
                </Alert>
            )}
            <form className="flex max-w-2xl shrink-0 items-end gap-3" onSubmit={createGroup}>
                <label className="min-w-0 flex-1 space-y-2">
                    <span className="text-xs text-muted-foreground">Название группы изображений</span>
                    <Input required maxLength={120} aria-label="Название группы" placeholder="Новая группа изображений" data-testid="image-group-new-name" value={name} onChange={event => setName(event.target.value)} />
                </label>
                <Button type="submit" data-testid="image-group-create" disabled={busy || !name.trim()}>Создать группу</Button>
            </form>
            <div className="grid min-h-0 flex-1 grid-cols-[150px_minmax(0,1fr)] gap-5 lg:grid-cols-[220px_minmax(0,1fr)]">
                <section className="min-h-0 overflow-y-auto overscroll-contain rounded-md border p-2" aria-label="Группы изображений" data-testid="image-groups" tabIndex={0}>
                    <div className="space-y-2">
                        {groups.groups.map(item => (
                            <button
                                key={item.id}
                                type="button"
                                data-image-group-id={item.id}
                                data-archived={item.archivedAt ? "true" : undefined}
                                className={`w-full rounded-md px-3 py-3 text-left outline-none transition-colors focus-visible:ring-2 focus-visible:ring-ring ${item.id === groupId ? "bg-accent text-accent-foreground" : "hover:bg-accent/50"}`}
                                aria-pressed={item.id === groupId}
                                onClick={() => selectGroup(item.id)}
                            >
                                <span className="block text-sm font-medium break-words">{item.name}{item.archivedAt ? " · Архив" : ""}</span>
                                <span className="mt-1 block text-xs text-muted-foreground">
                                    {item.origin === "generated" ? "Генерация" : "Исходные изображения"} · {splitHintLabels[item.splitHint]}
                                </span>
                            </button>
                        ))}
                        {groups.loading && !groups.groups.length && <p role="status" className="p-2 text-sm text-muted-foreground">Загрузка групп…</p>}
                        {groups.error && (
                            <Alert variant="destructive">
                                <AlertDescription>
                                    <p>{groups.error}</p>
                                    <Button variant="ghost" size="sm" className="mt-1" onClick={() => void groups.refresh()}>Повторить</Button>
                                </AlertDescription>
                            </Alert>
                        )}
                        {!groups.loading && !groups.error && !groups.groups.length && (
                            <Empty>Создайте набор кадров: чистые фоны или изображения с разметкой.</Empty>
                        )}
                    </div>
                </section>
                <section className="flex min-h-0 min-w-0 flex-col gap-3 overflow-hidden" aria-label="Содержимое группы изображений" data-testid="image-group-content">
                    {group ? (
                        <>
                            <div className="flex shrink-0 flex-col gap-3">
                                <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
                                    <h2 className="min-w-0 font-medium break-words" data-testid="image-group-title">{group.name}{group.archivedAt ? " · Архив" : ""}</h2>
                                    <span className="text-xs text-muted-foreground tabular-nums" data-testid="image-group-count">
                                        Кадров: {activeCount.toLocaleString("ru-RU")}{group.archivedImageCount ? ` · в архиве: ${group.archivedImageCount.toLocaleString("ru-RU")}` : ""}
                                    </span>
                                    <span className="text-xs text-muted-foreground">{group.origin === "generated" ? "Генерация" : "Исходные изображения"} · {splitHintLabels[group.splitHint]}</span>
                                </div>
                                {group.description && <p className="max-w-prose text-xs text-muted-foreground break-words">{group.description}</p>}
                                <div className="flex flex-wrap items-center gap-2">
                                    <Button size="sm" disabled={busy || importLocked || imports.selecting} onClick={() => void imports.start("files", { skipDuplicates, recursive })}>
                                        <Upload />Импорт файлов
                                    </Button>
                                    <Button size="sm" variant="outline" disabled={busy || importLocked || imports.selecting} onClick={() => void imports.start("folder", { skipDuplicates, recursive })}>
                                        <FolderOpen />Из папки
                                    </Button>
                                    <Button size="sm" variant="outline" aria-pressed={videoOpen} data-testid="video-open" disabled={busy || importLocked || imports.selecting} onClick={() => setVideoOpen(open => !open)}>
                                        <Film />Кадры из видео
                                    </Button>
                                    <Button size="sm" variant="ghost" aria-label={`Изменить группу: ${group.name}`} disabled={busy || generating} onClick={() => { setDialogError(undefined); setEditGroup(group); }}>
                                        <Pencil />Изменить
                                    </Button>
                                    <Button size="sm" variant="ghost" aria-label={`${group.archivedAt ? "Восстановить" : "В архив"}: ${group.name}`} disabled={busy || generating} onClick={() => toggleGroupArchive(group)}>
                                        {group.archivedAt ? <><ArchiveRestore />Восстановить</> : <><Archive />В архив</>}
                                    </Button>
                                    <Button size="sm" variant="ghost" className="text-destructive hover:text-destructive" aria-label={`Удалить группу изображений: ${group.name}`} disabled={busy || generating} onClick={() => requestDeletion()}>
                                        <Trash2 />Удалить группу
                                    </Button>
                                </div>
                                {!editImage && (
                                    <div className="flex flex-wrap items-center gap-x-4 gap-y-2 text-xs text-muted-foreground">
                                        <label className="flex items-center gap-2">
                                            <Checkbox checked={skipDuplicates} onCheckedChange={value => setSkipDuplicates(value === true)} data-testid="import-skip-duplicates" />
                                            Пропускать повторы в группе
                                        </label>
                                        <label className="flex items-center gap-2">
                                            <Checkbox checked={recursive} onCheckedChange={value => setRecursive(value === true)} data-testid="import-recursive" />
                                            Включая вложенные папки
                                        </label>
                                    </div>
                                )}
                                {generating && <p className="text-sm text-muted-foreground">{generationLock}</p>}
                                {!editImage && (
                                    <div className="flex flex-wrap items-center gap-2">
                                        <Select value={filter.archive} onValueChange={value => changeFilter({ ...filter, archive: value as ImageArchiveFilter })}>
                                            <SelectTrigger size="sm" aria-label="Показ архива" className="w-40" data-testid="filter-archive"><SelectValue /></SelectTrigger>
                                            <SelectContent>
                                                <SelectItem value="active">Активные</SelectItem>
                                                <SelectItem value="archived">Архивные</SelectItem>
                                                <SelectItem value="all">Все кадры</SelectItem>
                                            </SelectContent>
                                        </Select>
                                        <Select value={reviewFilterValue(filter.reviewState)} onValueChange={value => changeFilter({ ...filter, reviewState: value === "any" ? null : value as ImageReviewState })}>
                                            <SelectTrigger size="sm" aria-label="Проверка разметки" className="w-40" data-testid="filter-review"><SelectValue /></SelectTrigger>
                                            <SelectContent>
                                                <SelectItem value="any">Любая проверка</SelectItem>
                                                <SelectItem value="reviewed">Проверено</SelectItem>
                                                <SelectItem value="unreviewed">Не проверено</SelectItem>
                                            </SelectContent>
                                        </Select>
                                        <Button size="sm" variant="ghost" disabled={!items.length || generating} data-testid="select-page" onClick={selection.selectPage}>
                                            {selected.size ? "Снять выделение" : "Выбрать все на странице"}
                                        </Button>
                                        {selected.size > 0 && (
                                            <>
                                                <Button size="sm" variant="outline" disabled={busy || generating || !!group.archivedAt || allArchived} onClick={() => { setDialogError(undefined); setMoving(true); }}>
                                                    <MoveRight />Переместить ({selected.size})
                                                </Button>
                                                <Button size="sm" variant="outline" disabled={busy || generating || !!group.archivedAt} data-testid="archive-selected" onClick={() => archiveSelected(!allArchived)}>
                                                    {allArchived ? <><ArchiveRestore />Восстановить ({selected.size})</> : <><Archive />В архив ({selected.size})</>}
                                                </Button>
                                                <Button size="sm" variant="outline" className="text-destructive hover:text-destructive" disabled={busy || generating} data-testid="delete-selected" onClick={() => requestDeletion([...selected])}>
                                                    <Trash2 />Удалить выбранные ({selected.size})
                                                </Button>
                                            </>
                                        )}
                                    </div>
                                )}
                            </div>
                            {editImage ? (
                                <div className="min-h-0 flex-1 overflow-y-auto overscroll-contain">
                                    <AnnotationEditor
                                        key={editImage.id}
                                        projectId={projectId}
                                        image={editImage}
                                        palette={palette}
                                        lockReason={generating ? generationLock : group.archivedAt || editImage.archivedAt ? "Группа или изображение в архиве. Восстановите их, чтобы изменить разметку." : undefined}
                                        navigation={editorNavigation}
                                        close={() => guard(() => setEditing(undefined))}
                                        inspect={(draft, revision) => setViewer({ imageId: editImage.id, draft, revision, fromEditor: true })}
                                        saved={saved}
                                        setColor={setColor}
                                    />
                                </div>
                            ) : (
                                <>
                                    {videoOpen && (
                                        <VideoFrameImport
                                            key={group.id}
                                            disabled={importLocked}
                                            skipDuplicates={skipDuplicates}
                                            setSkipDuplicates={setSkipDuplicates}
                                            importFrames={imports.importVideo}
                                            close={() => setVideoOpen(false)}
                                        />
                                    )}
                                    {!items.length && gallery.loading && <p role="status" className="text-sm text-muted-foreground">Загрузка изображений…</p>}
                                    {gallery.error && (
                                        <Alert variant="destructive" className="shrink-0">
                                            <AlertDescription>
                                                <p>{gallery.error}</p>
                                                <Button variant="ghost" size="sm" className="mt-1" onClick={() => void gallery.reload()}>Повторить</Button>
                                            </AlertDescription>
                                        </Alert>
                                    )}
                                    {!items.length && !gallery.loading && !gallery.error && (
                                        <Empty>
                                            {filter.archive === "active" && filter.reviewState === null
                                                ? "Добавьте готовые кадры или кадры из видео. Можно оставить чистый фон либо разметить объекты и сектора."
                                                : "Нет кадров с выбранными условиями."}
                                        </Empty>
                                    )}
                                    {items.length > 0 && (
                                        <ImageGallery
                                            items={items}
                                            annotations={gallery.annotations}
                                            selected={selected}
                                            hasMore={gallery.hasMore}
                                            loading={gallery.loading}
                                            locked={generating}
                                            busy={busy}
                                            palette={palette}
                                            actions={{
                                                toggle: selection.toggle,
                                                edit: openEditor,
                                                view,
                                                remove: image => requestDeletion([image.id]),
                                                renew: gallery.renew,
                                                loadMore: () => void gallery.loadMore(),
                                            }}
                                        />
                                    )}
                                </>
                            )}
                            {!editImage && <ImageImportsPanel imports={imports} />}
                        </>
                    ) : (
                        <Empty>{groups.groups.length ? "Выберите группу слева." : "Создайте группу для импорта кадров и разметки."}</Empty>
                    )}
                </section>
            </div>
            {viewer && viewed && (
                <ImageViewer
                    source={{
                        id: viewed.id,
                        name: viewed.displayName,
                        width: viewed.width,
                        height: viewed.height,
                        src: viewed.originalUrl,
                        renew: () => gallery.renew(viewed.id),
                        overlays: annotationOverlays(shownAnnotation, palette, showObjects, showRoi),
                        metadata: (
                            <div className="space-y-2">
                                <div className="flex flex-wrap gap-3">
                                    <label className="flex items-center gap-2 text-xs">
                                        <Checkbox aria-label="Рамки объектов" checked={showObjects} onCheckedChange={value => setOverlays({ imageId: viewed.id, objects: value === true, roi: showRoi })} />
                                        Рамки объектов
                                    </label>
                                    <label className="flex items-center gap-2 text-xs">
                                        <Checkbox aria-label="Область ROI" checked={showRoi} onCheckedChange={value => setOverlays({ imageId: viewed.id, objects: showObjects, roi: value === true })} />
                                        Область ROI
                                    </label>
                                </div>
                                <ImageDetails image={viewed} annotation={shownAnnotation} revision={shownRevision} palette={palette} />
                            </div>
                        ),
                    }}
                    close={() => setViewer(undefined)}
                    onPrevious={!viewer.fromEditor && viewerIndex > 0 ? () => void viewNeighbour(-1) : undefined}
                    onNext={!viewer.fromEditor && (viewerIndex < items.length - 1 || gallery.hasMore) ? () => void viewNeighbour(1) : undefined}
                    navigationBusy={viewerBusy}
                    position={viewer.fromEditor ? undefined : `${viewerIndex + 1} / ${items.length}${gallery.hasMore ? "+" : ""}`}
                />
            )}
            {editGroup && <ImageGroupDialog group={editGroup} busy={busy} error={dialogError} close={() => setEditGroup(undefined)} save={saveGroup} />}
            {moving && (
                <MoveImagesDialog count={selected.size} targets={moveTargets} busy={busy} error={dialogError} close={() => setMoving(false)} move={moveSelected} />
            )}
            {deletion && <ImagesDeletionDialog projectId={projectId} target={deletion} close={() => setDeletion(undefined)} deleted={deleted} />}
        </div>
    );
}

function Empty({ children }: { children: ReactNode }) {
    return (
        <div className="flex min-h-40 flex-col items-center justify-center rounded-md border border-dashed p-6 text-center text-sm text-muted-foreground" data-testid="images-empty">
            {children}
        </div>
    );
}
