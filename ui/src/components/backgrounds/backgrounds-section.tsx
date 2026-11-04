import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { Archive, ArchiveRestore, EyeOff, Pencil, Trash2, TriangleAlert } from "lucide-react";
import { backgroundsApi } from "@/api/projects/backgrounds/backgrounds-api";
import type { ImportBackgroundsRequestDto } from "@/api/projects/backgrounds/request-dto/import-backgrounds-request-dto";
import type { BackgroundDeletionResultResponseDto } from "@/api/projects/backgrounds/response-dto/background-deletion-response-dto";
import type { BackgroundResponseDto } from "@/api/projects/backgrounds/response-dto/background-response-dto";
import type { BackgroundSetResponseDto } from "@/api/projects/backgrounds/response-dto/background-set-response-dto";
import type { ProjectResponseDto } from "@/api/projects/response-dto/project-response-dto";
import { BridgeError } from "@/api/transport/errors";
import { CollectionCreateForm, CollectionItem, CollectionList } from "@/components/collections/collection-list";
import { ActiveJobs } from "@/components/jobs/job-history";
import { EmptyState } from "@/components/layout/empty-state";
import { MasterDetail } from "@/components/layout/master-detail";
import { Notice } from "@/components/layout/notice";
import { Pane } from "@/components/layout/pane";
import { SectionLayout } from "@/components/layout/section-layout";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/layout/tabs";
import { Toolbar } from "@/components/layout/toolbar";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { ImageViewer } from "@/components/viewer/image-viewer";
import { backgroundDetails, setSummary, sizeMismatchText } from "@/lib/backgrounds/background-text";
import { errorMessage, formatNumber } from "@/lib/format";
import { useBackgroundBlur } from "@/lib/use-background-blur";
import { useBackgroundGallery } from "@/lib/use-background-gallery";
import { useBackgroundRuns } from "@/lib/use-background-runs";
import { useBackgroundSets } from "@/lib/use-background-sets";
import { selectionLimit, useRangeSelection } from "@/lib/use-range-selection";
import { cn } from "@/lib/utils";
import { BackgroundAddPanel, type FileSelection } from "./background-add-panel";
import { BackgroundGallery } from "./background-gallery";
import { BackgroundImageCropDialog } from "./background-image-crop-dialog";
import { BackgroundRuns } from "./background-runs-panel";
import { BackgroundSetDialog, type BackgroundSetValues } from "./background-set-dialog";
import { BackgroundsDeletionDialog, type BackgroundsDeletionTarget } from "./backgrounds-deletion-dialog";
import "@/i18n/sections/backgrounds";

const conflictCodes = /\.(revision_conflict|deletion_revision_conflict)$/;

type NoticeState = { tone: "info" | "error"; text: string };

/** Tabs of a set: the gallery, the add form with the running tasks beside it, the preparation history. */
type SetTab = "backgrounds" | "add" | "runs";

/**
 * The backgrounds section: sets on the left, the chosen set on the right: a header with its actions and three tabs: the virtual gallery,
 * "Add" (three ways and a shared blur, the running tasks of the set beside the form) and the preparation history. No polling:
 * backgrounds.changed, job.changed and project.changed (the size mismatch mark).
 */
export function BackgroundsSection({ project }: { project: ProjectResponseDto }) {
    const { t } = useTranslation("backgrounds");
    const projectId = project.id;
    const sets = useBackgroundSets(projectId);
    const [setId, setSetId] = useState<string>();
    const [showRemoved, setShowRemoved] = useState(false);
    const set = sets.sets.find(item => item.id === setId);
    const filter = showRemoved ? "all" : "active";
    const gallery = useBackgroundGallery(projectId, set?.id, filter);
    const runs = useBackgroundRuns(projectId, set?.id);
    const blur = useBackgroundBlur();
    const [notice, setNotice] = useState<NoticeState>();
    const items = gallery.items;
    const selection = useRangeSelection(items, `${setId}:${filter}`, {
        limit: t("selection.limit", { count: selectionLimit }),
        firstPage: t("selection.firstPage", { count: selectionLimit }),
    }, text => setNotice({ tone: "info", text }));
    const selected = selection.selected;
    const [busy, setBusy] = useState(false);
    const [name, setName] = useState("");
    const [tab, setTab] = useState<SetTab>("backgrounds");
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

    function describe(cause: unknown): string {
        const message = errorMessage(cause);
        if (cause instanceof BridgeError && conflictCodes.test(cause.code)) {
            if (setId) void sets.reread(setId);
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

    /** A chosen set opens on "Add" when it is empty and on the gallery otherwise; later counts do not switch the tab under the user. */
    function selectSet(target: BackgroundSetResponseDto) {
        if (target.id === setId) return;
        setSetId(target.id);
        setTab(target.itemCount + target.removedCount === 0 ? "add" : "backgrounds");
        setViewerId(undefined);
        setNotice(undefined);
    }

    function createSet(value: string) {
        setNotice(undefined);
        void mutate(async () => {
            const created = await backgroundsApi.createSet({ projectId, name: value, description: "" });
            sets.apply(created);
            setName("");
            selectSet(created);
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

    /**
     * Preparation: the job starts at once; its progress and result come to the set history with job.changed events. The user stays on
     * "Add": the running job shows in the task column beside the form, or in the strip above it in a narrow window.
     */
    async function start(request: ImportBackgroundsRequestDto) {
        const scope = runs.scope();
        const job = await backgroundsApi.import(request);
        if (runs.isCurrent(scope)) runs.apply(job);
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
            ? { tone: "info", text: t("section.cleanupPending") }
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

    // An archived set cannot be added to: "Add" is disabled and shows the gallery; restoring the set returns to the chosen tab.
    const shownTab: SetTab = archived && tab === "add" ? "backgrounds" : tab;

    return (
        <SectionLayout data-testid="backgrounds-section">
            {notice && (
                <Notice variant={notice.tone === "error" ? "destructive" : "default"} className="shrink-0" data-testid="backgrounds-notice" onDismiss={() => setNotice(undefined)}>
                    {notice.text}
                </Notice>
            )}
            <CollectionCreateForm
                label={t("section.newSetLabel")}
                placeholder={t("section.newSetPlaceholder")}
                submitText={t("section.createSet")}
                value={name}
                setValue={setName}
                busy={busy}
                submit={createSet}
                inputTestId="background-set-new-name"
                submitTestId="background-set-create"
            />
            <MasterDetail>
                <CollectionList
                    label={t("section.sets")}
                    testId="background-sets"
                    loading={sets.loading && !sets.sets.length}
                    loadingText={t("section.loadingSets")}
                    error={sets.error}
                    retry={() => void sets.refresh()}
                    retryText={t("section.retry")}
                    empty={!sets.loading && !sets.error && !sets.sets.length && <Empty>{t("section.noSets")}</Empty>}
                >
                    {sets.sets.map(item => (
                        <CollectionItem
                            key={item.id}
                            data-background-set-id={item.id}
                            data-archived={item.archivedAt ? "true" : undefined}
                            selected={item.id === setId}
                            onClick={() => selectSet(item)}
                            title={item.name}
                            summary={<span className="mt-1 block text-xs text-text-2" data-testid="background-set-summary">{setSummary(item)}</span>}
                        >
                            {!item.matchesProjectInput && (
                                <span className="mt-1 flex items-center gap-1 text-xs text-warn" title={sizeMismatchText(item)} data-testid="set-size-mismatch">
                                    <TriangleAlert className="size-3.5 shrink-0" aria-hidden />{t("section.sizeDiffers")}
                                </span>
                            )}
                        </CollectionItem>
                    ))}
                </CollectionList>
                <section className="flex min-h-0 min-w-0 flex-col gap-2" aria-label={t("section.setContent")} data-testid="background-set-content">
                    {set ? (
                        <>
                            <div className="flex min-w-0 shrink-0 flex-col gap-1">
                                <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
                                    <h2 className="min-w-0 font-medium break-words" data-testid="background-set-title">{set.name}</h2>
                                    <span className="text-xs text-text-2 tabular-nums" data-testid="background-set-count">
                                        {set.removedCount ? t("section.summaryRemoved", { summary: setSummary(set), removed: formatNumber(set.removedCount) }) : setSummary(set)}
                                    </span>
                                    {set.description && <span className="min-w-0 flex-1 truncate text-xs text-text-2" title={set.description}>{set.description}</span>}
                                    <Toolbar className="ml-auto gap-1">
                                        <Button size="sm" variant="ghost" aria-label={t("section.editSetLabel", { name: set.name })} title={t("section.editSetLabel", { name: set.name })} disabled={busy} onClick={() => { setDialogError(undefined); setEditSet(set); }}>
                                            <Pencil /><span className="hidden lg:inline">{t("section.edit")}</span>
                                        </Button>
                                        <Button size="sm" variant="ghost" aria-label={t(set.archivedAt ? "section.restoreSetLabel" : "section.archiveSetLabel", { name: set.name })} title={t(set.archivedAt ? "section.restoreSetLabel" : "section.archiveSetLabel", { name: set.name })} disabled={busy} onClick={() => toggleArchive(set)}>
                                            {set.archivedAt ? <><ArchiveRestore /><span className="hidden lg:inline">{t("section.restore")}</span></> : <><Archive /><span className="hidden lg:inline">{t("section.archive")}</span></>}
                                        </Button>
                                        <Button size="sm" variant="ghost" className="text-destructive hover:text-destructive" aria-label={t("section.deleteSetLabel", { name: set.name })} title={t("section.deleteSetLabel", { name: set.name })} disabled={busy}
                                            onClick={() => requestDeletion()}>
                                            <Trash2 /><span className="hidden lg:inline">{t("section.deleteSet")}</span>
                                        </Button>
                                    </Toolbar>
                                </div>
                                {!set.matchesProjectInput && (
                                    <p className="flex min-w-0 items-center gap-2 text-xs text-warn" data-testid="set-size-mismatch-note"
                                        title={t("section.sizeMismatchNote", { width: set.targetSize.width, height: set.targetSize.height, inputWidth: project.inputSize.width, inputHeight: project.inputSize.height })}>
                                        <TriangleAlert className="size-4 shrink-0" aria-hidden />
                                        <span className="truncate">{t("section.sizeMismatchNote", { width: set.targetSize.width, height: set.targetSize.height, inputWidth: project.inputSize.width, inputHeight: project.inputSize.height })}</span>
                                    </p>
                                )}
                                {archived && <p className="truncate text-xs text-text-2" title={t("section.archivedNote")}>{t("section.archivedNote")}</p>}
                            </div>
                            <Tabs value={shownTab} onValueChange={value => setTab(value as SetTab)} className="flex-1 gap-2">
                                <TabsList aria-label={t("tabs.label")}>
                                    <TabsTrigger value="backgrounds" count={set.itemCount} data-testid="background-tab-backgrounds">{t("tabs.backgrounds")}</TabsTrigger>
                                    <TabsTrigger value="add" disabled={archived} data-testid="background-tab-add">{t("tabs.add")}</TabsTrigger>
                                    <TabsTrigger value="runs" count={runs.jobs.length} live={runs.active} data-testid="background-tab-runs">{t("tabs.runs")}</TabsTrigger>
                                </TabsList>
                                <TabsContent value="backgrounds" className="flex flex-col gap-2">
                                    <Toolbar className="shrink-0">
                                        {items.length > 0 && (
                                            <Button size="sm" variant="ghost" data-testid="background-select-page" onClick={selection.selectPage}>
                                                {selected.size ? t("selection.clear") : t("selection.page")}
                                            </Button>
                                        )}
                                        {selected.size > 0 && (
                                            <>
                                                <Button size="sm" variant="outline" disabled={busy || archived} data-testid="background-remove-selected" onClick={() => setRemoved(selectedItems, !allRemoved)}>
                                                    {allRemoved ? <><ArchiveRestore />{t("selection.restore", { number: selected.size })}</> : <><EyeOff />{t("selection.remove", { number: selected.size })}</>}
                                                </Button>
                                                <Button size="sm" variant="outline" className="text-destructive hover:text-destructive" disabled={busy} data-testid="background-delete-selected"
                                                    onClick={() => requestDeletion([...selected], t("selection.deletionName", { name: set.name }))}>
                                                    <Trash2 />{t("selection.delete", { number: selected.size })}
                                                </Button>
                                            </>
                                        )}
                                        <label className="ml-auto flex items-center gap-2 text-xs text-text-2">
                                            <Checkbox checked={showRemoved} data-testid="show-removed" onCheckedChange={value => setShowRemoved(value === true)} />
                                            {t("section.showRemoved")}
                                        </label>
                                    </Toolbar>
                                    {!items.length && gallery.loading && <p role="status" className="text-sm text-text-2">{t("section.loadingBackgrounds")}</p>}
                                    {gallery.error && (
                                        <Notice variant="destructive" className="shrink-0" action={{ label: t("section.retry"), onClick: () => void gallery.reload() }}>{gallery.error}</Notice>
                                    )}
                                    {!items.length && !gallery.loading && !gallery.error && <Empty className="flex-1">{t("section.emptySet")}</Empty>}
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
                                </TabsContent>
                                {/* Mounted while hidden: the chosen files, the video, its frame and the fields survive switching tabs. */}
                                <TabsContent value="add" forceMount hidden={shownTab !== "add"}
                                    className="flex flex-col gap-3 lg:grid lg:grid-cols-[minmax(0,1fr)_minmax(15rem,19rem)] lg:grid-rows-[minmax(0,1fr)]">
                                    <ActiveJobs jobs={runs.jobs} label={t("runs.title")} progressLabel={t("runs.progress")} className="lg:hidden" testId="background-active-runs"
                                        action={{ label: t("tabs.showRuns"), onClick: () => setTab("runs") }} />
                                    <BackgroundAddPanel
                                        key={set.id}
                                        projectId={projectId}
                                        set={set}
                                        blur={blur}
                                        disabled={archived || busy}
                                        start={start}
                                        openCrop={setCrop}
                                    />
                                    {shownTab === "add" && (
                                        <Pane surface className="hidden p-3 lg:block" data-testid="background-add-runs">
                                            <BackgroundRuns runs={runs} />
                                        </Pane>
                                    )}
                                </TabsContent>
                                <TabsContent value="runs" className="flex flex-col">
                                    <Pane surface className="flex-1 p-3">
                                        <BackgroundRuns runs={runs} />
                                    </Pane>
                                </TabsContent>
                            </Tabs>
                        </>
                    ) : (
                        <Empty className="flex-1">{sets.sets.length ? t("section.chooseSet") : t("section.createFirstSet")}</Empty>
                    )}
                </section>
            </MasterDetail>
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
        </SectionLayout>
    );
}

function Empty({ className, children }: { className?: string; children: string }) {
    return <EmptyState title={children} className={cn("rounded-lg border border-dashed border-line", className)} data-testid="backgrounds-empty" />;
}
