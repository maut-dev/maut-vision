import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
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
import { errorMessage, formatNumber } from "@/lib/format";
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
import "./locale";

const conflictCodes = /\.(revision_conflict|deletion_revision_conflict)$/;

type Notice = { tone: "info" | "error"; text: string };

interface ViewerState {
    imageId: string;
    /** Draft from the editor; without it the saved annotation is shown. */
    draft?: AnnotationDraft;
    revision?: number;
    fromEditor: boolean;
}

const reviewFilterValue = (value: ImageReviewState | null) => value ?? "any";

/**
 * The images section: groups on the left, the gallery of the chosen group or the annotation editor on the right. Every transition
 * that could lose an unsaved annotation goes through the shared guard (useNavigationGuard). initialGroupId is the group chosen when
 * the section opens (for example, "Open images" of a generation run).
 */
export function ImagesSection({ project, initialGroupId }: { project: ProjectResponseDto; initialGroupId?: string }) {
    const { t } = useTranslation("images");
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
        limit: t("selection.limit", { count: selectionLimit }),
        firstPage: t("selection.firstPage", { count: selectionLimit }),
    }, text => setNotice({ tone: "info", text }));
    const selected = selection.selected;
    const editImage = editing ? items.find(item => item.id === editing.id) ?? editing : undefined;
    const generating = group?.generationState === "producing";
    const importLocked = !group || generating || !!group.archivedAt || group.origin !== "manual";
    const selectedImages = items.filter(item => selected.has(item.id));
    const activeCount = group ? group.imageCount - group.archivedImageCount : 0;
    const generationLock = t("section.generationLock");

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
            return t("section.listUpdated", { message });
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
            setNotice({ tone: "info", text: t("section.moved", { name: target.name, number: formatNumber(result.images.length) }) });
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
        setNotice(result.cleanupPending ? { tone: "info", text: t("section.cleanupPending") } : undefined);
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

    /** The neighbour in the gallery order; after the last loaded one, the first image of the next page. */
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
                        <Button variant="ghost" size="sm" className="mt-1" onClick={() => setNotice(undefined)}>{t("section.hide")}</Button>
                    </AlertDescription>
                </Alert>
            )}
            <form className="flex max-w-2xl shrink-0 items-end gap-3" onSubmit={createGroup}>
                <label className="min-w-0 flex-1 space-y-2">
                    <span className="text-xs text-muted-foreground">{t("section.newGroupLabel")}</span>
                    <Input required maxLength={120} aria-label={t("section.newGroupName")} placeholder={t("section.newGroupPlaceholder")} data-testid="image-group-new-name" value={name} onChange={event => setName(event.target.value)} />
                </label>
                <Button type="submit" data-testid="image-group-create" disabled={busy || !name.trim()}>{t("section.createGroup")}</Button>
            </form>
            <div className="grid min-h-0 flex-1 grid-cols-[150px_minmax(0,1fr)] gap-5 lg:grid-cols-[220px_minmax(0,1fr)]">
                <section className="min-h-0 overflow-y-auto overscroll-contain rounded-md border p-2" aria-label={t("section.groups")} data-testid="image-groups" tabIndex={0}>
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
                                <span className="block text-sm font-medium break-words">{item.archivedAt ? t("section.archivedName", { name: item.name }) : item.name}</span>
                                <span className="mt-1 block text-xs text-muted-foreground">
                                    {t("section.groupKind", { origin: t(`origin.${item.origin}`), split: splitHintLabels[item.splitHint] })}
                                </span>
                            </button>
                        ))}
                        {groups.loading && !groups.groups.length && <p role="status" className="p-2 text-sm text-muted-foreground">{t("section.loadingGroups")}</p>}
                        {groups.error && (
                            <Alert variant="destructive">
                                <AlertDescription>
                                    <p>{groups.error}</p>
                                    <Button variant="ghost" size="sm" className="mt-1" onClick={() => void groups.refresh()}>{t("section.retry")}</Button>
                                </AlertDescription>
                            </Alert>
                        )}
                        {!groups.loading && !groups.error && !groups.groups.length && (
                            <Empty>{t("section.noGroups")}</Empty>
                        )}
                    </div>
                </section>
                <section className="flex min-h-0 min-w-0 flex-col gap-3 overflow-hidden" aria-label={t("section.groupContent")} data-testid="image-group-content">
                    {group ? (
                        <>
                            <div className="flex shrink-0 flex-col gap-3">
                                <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
                                    <h2 className="min-w-0 font-medium break-words" data-testid="image-group-title">{group.archivedAt ? t("section.archivedName", { name: group.name }) : group.name}</h2>
                                    <span className="text-xs text-muted-foreground tabular-nums" data-testid="image-group-count">
                                        {group.archivedImageCount
                                            ? t("section.countWithArchive", { active: formatNumber(activeCount), archived: formatNumber(group.archivedImageCount) })
                                            : t("section.count", { active: formatNumber(activeCount) })}
                                    </span>
                                    <span className="text-xs text-muted-foreground">{t("section.groupKind", { origin: t(`origin.${group.origin}`), split: splitHintLabels[group.splitHint] })}</span>
                                </div>
                                {group.description && <p className="max-w-prose text-xs text-muted-foreground break-words">{group.description}</p>}
                                <div className="flex flex-wrap items-center gap-2">
                                    <Button size="sm" disabled={busy || importLocked || imports.selecting} onClick={() => void imports.start("files", { skipDuplicates, recursive })}>
                                        <Upload />{t("section.importFiles")}
                                    </Button>
                                    <Button size="sm" variant="outline" disabled={busy || importLocked || imports.selecting} onClick={() => void imports.start("folder", { skipDuplicates, recursive })}>
                                        <FolderOpen />{t("section.importFolder")}
                                    </Button>
                                    <Button size="sm" variant="outline" aria-pressed={videoOpen} data-testid="video-open" disabled={busy || importLocked || imports.selecting} onClick={() => setVideoOpen(open => !open)}>
                                        <Film />{t("section.videoFrames")}
                                    </Button>
                                    <Button size="sm" variant="ghost" aria-label={t("section.editGroupLabel", { name: group.name })} disabled={busy || generating} onClick={() => { setDialogError(undefined); setEditGroup(group); }}>
                                        <Pencil />{t("section.edit")}
                                    </Button>
                                    <Button size="sm" variant="ghost" aria-label={t(group.archivedAt ? "section.restoreGroupLabel" : "section.archiveGroupLabel", { name: group.name })} disabled={busy || generating} onClick={() => toggleGroupArchive(group)}>
                                        {group.archivedAt ? <><ArchiveRestore />{t("section.restore")}</> : <><Archive />{t("section.archive")}</>}
                                    </Button>
                                    <Button size="sm" variant="ghost" className="text-destructive hover:text-destructive" aria-label={t("section.deleteGroupLabel", { name: group.name })} disabled={busy || generating} onClick={() => requestDeletion()}>
                                        <Trash2 />{t("section.deleteGroup")}
                                    </Button>
                                </div>
                                {!editImage && (
                                    <div className="flex flex-wrap items-center gap-x-4 gap-y-2 text-xs text-muted-foreground">
                                        <label className="flex items-center gap-2">
                                            <Checkbox checked={skipDuplicates} onCheckedChange={value => setSkipDuplicates(value === true)} data-testid="import-skip-duplicates" />
                                            {t("section.skipDuplicates")}
                                        </label>
                                        <label className="flex items-center gap-2">
                                            <Checkbox checked={recursive} onCheckedChange={value => setRecursive(value === true)} data-testid="import-recursive" />
                                            {t("section.recursive")}
                                        </label>
                                    </div>
                                )}
                                {generating && <p className="text-sm text-muted-foreground">{generationLock}</p>}
                                {!editImage && (
                                    <div className="flex flex-wrap items-center gap-2">
                                        <Select value={filter.archive} onValueChange={value => changeFilter({ ...filter, archive: value as ImageArchiveFilter })}>
                                            <SelectTrigger size="sm" aria-label={t("filter.archiveLabel")} className="w-40" data-testid="filter-archive"><SelectValue /></SelectTrigger>
                                            <SelectContent>
                                                <SelectItem value="active">{t("filter.active")}</SelectItem>
                                                <SelectItem value="archived">{t("filter.archived")}</SelectItem>
                                                <SelectItem value="all">{t("filter.all")}</SelectItem>
                                            </SelectContent>
                                        </Select>
                                        <Select value={reviewFilterValue(filter.reviewState)} onValueChange={value => changeFilter({ ...filter, reviewState: value === "any" ? null : value as ImageReviewState })}>
                                            <SelectTrigger size="sm" aria-label={t("filter.reviewLabel")} className="w-40" data-testid="filter-review"><SelectValue /></SelectTrigger>
                                            <SelectContent>
                                                <SelectItem value="any">{t("filter.anyReview")}</SelectItem>
                                                <SelectItem value="reviewed">{t("review.reviewed")}</SelectItem>
                                                <SelectItem value="unreviewed">{t("review.unreviewed")}</SelectItem>
                                            </SelectContent>
                                        </Select>
                                        <Button size="sm" variant="ghost" disabled={!items.length || generating} data-testid="select-page" onClick={selection.selectPage}>
                                            {selected.size ? t("selection.clear") : t("selection.page")}
                                        </Button>
                                        {selected.size > 0 && (
                                            <>
                                                <Button size="sm" variant="outline" disabled={busy || generating || !!group.archivedAt || allArchived} onClick={() => { setDialogError(undefined); setMoving(true); }}>
                                                    <MoveRight />{t("selection.move", { number: selected.size })}
                                                </Button>
                                                <Button size="sm" variant="outline" disabled={busy || generating || !!group.archivedAt} data-testid="archive-selected" onClick={() => archiveSelected(!allArchived)}>
                                                    {allArchived ? <><ArchiveRestore />{t("selection.restore", { number: selected.size })}</> : <><Archive />{t("selection.archive", { number: selected.size })}</>}
                                                </Button>
                                                <Button size="sm" variant="outline" className="text-destructive hover:text-destructive" disabled={busy || generating} data-testid="delete-selected" onClick={() => requestDeletion([...selected])}>
                                                    <Trash2 />{t("selection.delete", { number: selected.size })}
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
                                        lockReason={generating ? generationLock : group.archivedAt || editImage.archivedAt ? t("section.archiveLock") : undefined}
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
                                    {!items.length && gallery.loading && <p role="status" className="text-sm text-muted-foreground">{t("section.loadingImages")}</p>}
                                    {gallery.error && (
                                        <Alert variant="destructive" className="shrink-0">
                                            <AlertDescription>
                                                <p>{gallery.error}</p>
                                                <Button variant="ghost" size="sm" className="mt-1" onClick={() => void gallery.reload()}>{t("section.retry")}</Button>
                                            </AlertDescription>
                                        </Alert>
                                    )}
                                    {!items.length && !gallery.loading && !gallery.error && (
                                        <Empty>
                                            {filter.archive === "active" && filter.reviewState === null
                                                ? t("section.emptyGroup")
                                                : t("section.emptyFilter")}
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
                        <Empty>{groups.groups.length ? t("section.chooseGroup") : t("section.createFirstGroup")}</Empty>
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
                                        <Checkbox aria-label={t("viewer.objects")} checked={showObjects} onCheckedChange={value => setOverlays({ imageId: viewed.id, objects: value === true, roi: showRoi })} />
                                        {t("viewer.objects")}
                                    </label>
                                    <label className="flex items-center gap-2 text-xs">
                                        <Checkbox aria-label={t("roi")} checked={showRoi} onCheckedChange={value => setOverlays({ imageId: viewed.id, objects: showObjects, roi: value === true })} />
                                        {t("roi")}
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
