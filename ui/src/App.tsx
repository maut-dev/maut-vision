import { lazy, Suspense, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import {
    Boxes,
    ChartLine,
    Cpu,
    Database,
    FolderOpen,
    Image,
    Layers,
    LayoutDashboard,
    ScanSearch,
    Settings,
    SlidersHorizontal,
    Tags,
    WandSparkles,
    type LucideIcon,
} from "lucide-react";
import { projectsApi } from "@/api/projects/projects-api";
import type { ProjectResponseDto } from "@/api/projects/response-dto/project-response-dto";
import { BaseModelCatalog } from "@/components/base-model-catalog";
import { CatalogSection } from "@/components/catalog/catalog-section";
import { BridgeError } from "@/api/transport/errors";
import { ProjectDeletionDialog } from "@/components/project-deletion-dialog";
import { ProjectEditor, type ProjectEditorValues } from "@/components/project-editor";
import { LanguageSelect } from "@/components/language-select";
import { ProjectLibrary } from "@/components/project-library";
import { OverviewSection, type OverviewTarget } from "@/components/overview/overview-section";
import { Alert, AlertDescription } from "@/components/ui/alert";
import {
    AlertDialog,
    AlertDialogAction,
    AlertDialogCancel,
    AlertDialogContent,
    AlertDialogDescription,
    AlertDialogFooter,
    AlertDialogHeader,
    AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { useNavigationGuard } from "@/components/unsaved-changes";
import { errorMessage } from "@/lib/format";
import { reportLibraryReady } from "@/lib/startup";
import { useBaseModels } from "@/lib/use-base-models";
import { useProjectTransfers } from "@/lib/use-project-transfers";
import { useProjects } from "@/lib/use-projects";

// The images section (gallery, editor, viewer) loads as its own chunk when first opened.
const ImagesSection = lazy(() => import("@/components/images/images-section").then(module => ({ default: module.ImagesSection })));
const BackgroundsSection = lazy(() => import("@/components/backgrounds/backgrounds-section").then(module => ({ default: module.BackgroundsSection })));
const GenerationSection = lazy(() => import("@/components/generation/generation-section").then(module => ({ default: module.GenerationSection })));
const DatasetsSection = lazy(() => import("@/components/datasets/datasets-section").then(module => ({ default: module.DatasetsSection })));
const TrainingSection = lazy(() => import("@/components/training/training-section").then(module => ({ default: module.TrainingSection })));
const ModelsSection = lazy(() => import("@/components/models/models-section").then(module => ({ default: module.ModelsSection })));
const ChecksSection = lazy(() => import("@/components/checks/checks-section").then(module => ({ default: module.ChecksSection })));
const AnalyticsSection = lazy(() => import("@/components/analytics/analytics-section").then(module => ({ default: module.AnalyticsSection })));

type SectionId = "overview" | "variants" | "classes" | "backgrounds" | "generation" | "images" | "datasets" | "training" | "models" | "checks" | "analytics";

/** A section of the project; its label and description come from the common catalog (sections.<id>). */
interface Section {
    id: SectionId;
    icon: LucideIcon;
    available: boolean;
}

const sections: readonly Section[] = [
    { id: "overview", icon: LayoutDashboard, available: true },
    { id: "variants", icon: SlidersHorizontal, available: true },
    { id: "classes", icon: Tags, available: true },
    { id: "backgrounds", icon: Layers, available: true },
    { id: "generation", icon: WandSparkles, available: true },
    { id: "images", icon: Image, available: true },
    { id: "datasets", icon: Database, available: true },
    { id: "training", icon: Cpu, available: true },
    { id: "models", icon: Boxes, available: true },
    { id: "checks", icon: ScanSearch, available: true },
    { id: "analytics", icon: ChartLine, available: true },
];

/**
 * Экран — состояние React, адрес страницы не меняется (см. docs/UI/navigation.md). groupId — намерение перехода
 * «Открыть изображения»: раздел изображений открывается с этой группой; versionId — паспорт версии в «Моделях»; runId — запуск во
 * вкладке «Текущий запуск» обучения; checksMode — режим «Датасет» проверок. Переход из боковой панели намерения не несёт.
 */
type View =
    | { kind: "library" }
    | { kind: "catalog" }
    | { kind: "project"; projectId: string; section: string; groupId?: string; versionId?: string; runId?: string; checksMode?: "dataset" };

interface EditorState {
    project?: ProjectResponseDto;
    modelId?: string;
}

export function App() {
    const { t } = useTranslation();
    const library = useProjects();
    const catalog = useBaseModels();
    const [view, setView] = useState<View>({ kind: "library" });
    const [editor, setEditor] = useState<EditorState>();
    const [editorError, setEditorError] = useState<string>();
    const [busy, setBusy] = useState(false);
    const [notice, setNotice] = useState<string>();
    const [archiveTarget, setArchiveTarget] = useState<ProjectResponseDto>();
    const [deleteTarget, setDeleteTarget] = useState<ProjectResponseDto>();
    const [deletionNotice, setDeletionNotice] = useState(false);
    const [opening, setOpening] = useState<string>();
    const openSequence = useRef(0);
    const guard = useNavigationGuard();
    // Импортированный проект открывается сам, только если пользователь в библиотеке и ничего в ней не начал.
    const transfers = useProjectTransfers(library.projects, !library.loading && !library.error, projectId => {
        if (view.kind === "library" && !editor && !deleteTarget && !archiveTarget && !opening && !busy) void openProject(projectId);
    });

    const project = view.kind === "project" ? library.projects.find(item => item.id === view.projectId) : undefined;
    const section = sections.find(item => view.kind === "project" && item.id === view.section) ?? sections[0];
    const activeProjects = library.projects.filter(item => !item.archivedAt);
    // Catalog sections fit the window: their lists scroll independently instead of the page.
    const bounded = !!project && view.kind === "project" && (section.id === "variants" || section.id === "classes" || section.id === "images" || section.id === "backgrounds" || section.id === "generation" || section.id === "datasets");

    useEffect(() => {
        // An archived or vanished project cannot stay open; the library shows its current state.
        if (view.kind === "project" && !library.loading && (!project || project.archivedAt)) setView({ kind: "library" });
    }, [view, project, library.loading]);

    useEffect(() => {
        if (!library.loading && !library.error) reportLibraryReady(library.projects.length);
    }, [library.loading, library.error, library.projects.length]);

    /**
     * Номер перехода растёт и между перезагрузками страницы (от времени): host сравнивает его с последним известным. Побеждает последний
     * переход — медленное открытие не держит переключение, его ответ (или projects.open_superseded) отбрасывается.
     */
    function nextOpenSequence(): number {
        openSequence.current = Math.max(Date.now(), openSequence.current + 1);
        return openSequence.current;
    }

    async function openProject(projectId: string) {
        const sequence = nextOpenSequence();
        setOpening(projectId);
        setNotice(undefined);
        try {
            const opened = await projectsApi.open({ projectId, sequence });
            if (sequence !== openSequence.current) return;
            library.opened(opened);
            setView({ kind: "project", projectId, section: "overview" });
        } catch (cause) {
            if (sequence !== openSequence.current || (cause instanceof BridgeError && cause.code === "projects.open_superseded")) return;
            setNotice(errorMessage(cause));
        } finally {
            if (sequence === openSequence.current) setOpening(undefined);
        }
    }

    /** Другой переход отменяет ожидание открытия: его ответ больше не меняет экран. */
    function supersedeOpen() {
        nextOpenSequence();
        setOpening(undefined);
    }

    async function saveProject(values: ProjectEditorValues) {
        const target = editor?.project;
        setBusy(true);
        setEditorError(undefined);
        try {
            const saved = target
                ? await projectsApi.update({ projectId: target.id, revision: target.revision, ...values })
                : await projectsApi.create(values);
            library.apply(saved);
            setEditor(undefined);
            if (!target) await openProject(saved.id);
        } catch (cause) {
            setEditorError(errorMessage(cause));
        } finally {
            setBusy(false);
        }
    }

    async function setArchived(target: ProjectResponseDto, archived: boolean) {
        setBusy(true);
        setNotice(undefined);
        try {
            library.apply(await projectsApi.setArchived({ projectId: target.id, archived }));
            setArchiveTarget(undefined);
        } catch (cause) {
            setNotice(errorMessage(cause));
        } finally {
            setBusy(false);
        }
    }

    function editProject(target?: ProjectResponseDto, modelId?: string) {
        setEditorError(undefined);
        setEditor({ project: target, modelId });
    }

    function showCatalog() {
        guard(() => {
            supersedeOpen();
            setEditor(undefined);
            setView({ kind: "catalog" });
            void catalog.refresh();
        });
    }

    /** Переход между экранами: несохранённая разметка сначала требует решения пользователя. */
    function navigate(next: View) {
        guard(() => {
            supersedeOpen();
            setView(next);
        });
    }

    return (
        <div className="flex h-full flex-col bg-background text-foreground">
            <header className="flex h-14 shrink-0 items-center justify-between gap-4 border-b bg-card px-4">
                <div className="flex min-w-0 items-center gap-3">
                    <img src="./brand/logo-mark.png" alt="Maut Vision" className="size-8 shrink-0 object-contain" draggable={false} />
                    <span className="hidden font-semibold tracking-tight sm:inline">Maut Vision</span>
                    <div className="h-6 border-l" />
                    <Button size="sm" variant="ghost" onClick={() => navigate({ kind: "library" })} disabled={busy} aria-current={view.kind === "library" ? "page" : undefined}>
                        <FolderOpen />{t("app.allProjects")}
                    </Button>
                    {project && (
                        <>
                            <span className="text-muted-foreground" aria-hidden>/</span>
                            <Select value={project.id} onValueChange={id => guard(() => void openProject(id))} disabled={busy}>
                                <SelectTrigger aria-label={t("app.switchProject")} className="w-44 sm:w-64"><SelectValue /></SelectTrigger>
                                <SelectContent>
                                    {activeProjects.map(item => <SelectItem key={item.id} value={item.id}>{item.name}</SelectItem>)}
                                </SelectContent>
                            </Select>
                        </>
                    )}
                </div>
                <div className="flex shrink-0 items-center gap-2">
                    {project && (
                        <Button size="icon-sm" variant="ghost" aria-label={t("app.projectSettings")} title={t("app.projectSettings")} disabled={busy} onClick={() => editProject(project)}>
                            <Settings />
                        </Button>
                    )}
                    <Button size="sm" variant={view.kind === "catalog" ? "secondary" : "ghost"} onClick={showCatalog} disabled={busy} aria-current={view.kind === "catalog" ? "page" : undefined}>
                        <Boxes />{t("app.baseModels")}
                    </Button>
                    <LanguageSelect disabled={busy} failed={setNotice} />
                    <span className="hidden text-xs text-muted-foreground lg:block">{t("app.localStorage")}</span>
                </div>
            </header>
            <div className="flex min-h-0 flex-1">
                {project && view.kind === "project" && (
                    <aside className="flex min-h-0 w-16 shrink-0 flex-col overflow-y-auto border-r bg-card p-2 md:w-52 md:p-3">
                        <nav aria-label={t("app.projectSections")} className="space-y-1">
                            {sections.map(({ id, icon: Icon, available }) => { const label = t(`sections.${id}.label`); return (
                                <Button
                                    key={id}
                                    data-section={id}
                                    aria-current={view.section === id ? "page" : undefined}
                                    title={available ? label : t("app.comingLater", { label })}
                                    aria-label={label}
                                    variant={view.section === id ? "secondary" : "ghost"}
                                    className="w-full justify-center gap-3 md:justify-start"
                                    disabled={busy || !available}
                                    onClick={() => { if (view.section !== id) navigate({ kind: "project", projectId: project.id, section: id }); }}
                                >
                                    <Icon className="size-4" />
                                    <span className="hidden md:inline">{label}</span>
                                    {!available && <span className="ml-auto hidden text-[11px] text-muted-foreground md:inline">{t("app.soon")}</span>}
                                </Button>
                            ); })}
                        </nav>
                    </aside>
                )}
                <main className={`min-h-0 min-w-0 flex-1 overscroll-contain ${bounded ? "flex flex-col overflow-hidden" : "overflow-y-auto"}`} aria-busy={busy}>
                    <div className={`mx-auto w-full px-5 py-6 md:px-8 ${bounded ? "flex min-h-0 flex-1 flex-col gap-5 overflow-hidden max-w-7xl" : `space-y-5 ${project ? "max-w-6xl" : "max-w-5xl"}`}`}>
                        {view.kind === "library" && (
                            <ProjectLibrary
                                library={library}
                                transfers={transfers}
                                busy={busy}
                                opening={opening}
                                notice={deletionNotice ? t("projectDeletion.cleanupNotice") : undefined}
                                dismissNotice={() => setDeletionNotice(false)}
                                create={() => editProject()}
                                edit={target => editProject(target)}
                                open={target => void openProject(target.id)}
                                archive={target => target.archivedAt ? void setArchived(target, false) : setArchiveTarget(target)}
                                remove={target => { setDeletionNotice(false); setDeleteTarget(target); }}
                                catalog={showCatalog}
                            />
                        )}
                        {view.kind === "catalog" && (
                            <BaseModelCatalog catalog={catalog} back={() => navigate({ kind: "library" })} createProject={modelId => editProject(undefined, modelId)} />
                        )}
                        {project && view.kind === "project" && (
                            <>
                                <div className="shrink-0">
                                    <h1 className="text-2xl font-semibold break-words" data-testid="active-project-name">{project.name}</h1>
                                    <p className="mt-2 max-w-prose text-sm text-muted-foreground">{t(`sections.${section.id}.label`)} · {t(`sections.${section.id}.description`)}</p>
                                </div>
                                {section.id === "overview" && (
                                    <OverviewSection
                                        key={project.id}
                                        project={project}
                                        baseModel={catalog.models.find(model => model.id === project.baseModelId)}
                                        edit={() => editProject(project)}
                                        openCatalog={showCatalog}
                                        open={(target: OverviewTarget) => navigate({ kind: "project", projectId: project.id, ...target })}
                                    />
                                )}
                                {section.id === "images" && (
                                    <Suspense fallback={<p role="status" className="text-sm text-muted-foreground">{t("app.loadingSection")}</p>}>
                                        <ImagesSection key={project.id} project={project} initialGroupId={view.groupId} />
                                    </Suspense>
                                )}
                                {section.id === "backgrounds" && (
                                    <Suspense fallback={<p role="status" className="text-sm text-muted-foreground">{t("app.loadingSection")}</p>}>
                                        <BackgroundsSection key={project.id} project={project} />
                                    </Suspense>
                                )}
                                {section.id === "generation" && (
                                    <Suspense fallback={<p role="status" className="text-sm text-muted-foreground">{t("app.loadingSection")}</p>}>
                                        <GenerationSection
                                            key={project.id}
                                            project={project}
                                            openImages={groupId => navigate({ kind: "project", projectId: project.id, section: "images", groupId })}
                                        />
                                    </Suspense>
                                )}
                                {section.id === "datasets" && (
                                    <Suspense fallback={<p role="status" className="text-sm text-muted-foreground">{t("app.loadingSection")}</p>}>
                                        <DatasetsSection key={project.id} project={project} />
                                    </Suspense>
                                )}
                                {section.id === "training" && (
                                    <Suspense fallback={<p role="status" className="text-sm text-muted-foreground">{t("app.loadingSection")}</p>}>
                                        <TrainingSection
                                            key={`${project.id}:${view.runId ?? ""}`}
                                            project={project}
                                            initialRunId={view.runId}
                                            openDatasets={() => navigate({ kind: "project", projectId: project.id, section: "datasets" })}
                                        />
                                    </Suspense>
                                )}
                                {section.id === "models" && (
                                    <Suspense fallback={<p role="status" className="text-sm text-muted-foreground">{t("app.loadingSection")}</p>}>
                                        <ModelsSection
                                            key={`${project.id}:${view.versionId ?? ""}`}
                                            project={project}
                                            initialVersionId={view.versionId}
                                            openTraining={() => navigate({ kind: "project", projectId: project.id, section: "training" })}
                                        />
                                    </Suspense>
                                )}
                                {section.id === "checks" && (
                                    <Suspense fallback={<p role="status" className="text-sm text-muted-foreground">{t("app.loadingSection")}</p>}>
                                        <ChecksSection
                                            key={`${project.id}:${view.checksMode ?? ""}`}
                                            project={project}
                                            initialMode={view.checksMode}
                                            openModels={() => navigate({ kind: "project", projectId: project.id, section: "models" })}
                                        />
                                    </Suspense>
                                )}
                                {section.id === "analytics" && (
                                    <Suspense fallback={<p role="status" className="text-sm text-muted-foreground">{t("app.loadingSection")}</p>}>
                                        <AnalyticsSection
                                            key={project.id}
                                            project={project}
                                            openTraining={() => navigate({ kind: "project", projectId: project.id, section: "training" })}
                                            openPassport={versionId => navigate({ kind: "project", projectId: project.id, section: "models", versionId })}
                                        />
                                    </Suspense>
                                )}
                                {(section.id === "variants" || section.id === "classes") && (
                                    <CatalogSection
                                        key={project.id}
                                        project={project}
                                        page={section.id}
                                        openVariants={() => setView({ kind: "project", projectId: project.id, section: "variants" })}
                                    />
                                )}
                            </>
                        )}
                    </div>
                </main>
            </div>
            <div className="fixed bottom-4 right-4 z-40 max-w-sm" aria-live="polite">
                {notice && !editor && (
                    <Alert variant="destructive" className="bg-card">
                        <AlertDescription>
                            <p>{notice}</p>
                            <Button variant="ghost" size="sm" className="mt-2" onClick={() => setNotice(undefined)}>{t("app.close")}</Button>
                        </AlertDescription>
                    </Alert>
                )}
            </div>
            {editor && (
                <ProjectEditor
                    key={editor.project?.id ?? "new"}
                    project={editor.project}
                    initialModelId={editor.modelId}
                    catalog={catalog}
                    busy={busy}
                    error={editorError}
                    close={() => { setEditor(undefined); setEditorError(undefined); }}
                    save={values => void saveProject(values)}
                    openCatalog={showCatalog}
                />
            )}
            {deleteTarget && (
                <ProjectDeletionDialog
                    key={deleteTarget.id}
                    project={deleteTarget}
                    close={() => setDeleteTarget(undefined)}
                    deleted={result => {
                        setDeleteTarget(undefined);
                        library.remove(result.projectId);
                        if (result.cleanupPending) setDeletionNotice(true);
                    }}
                />
            )}
            <AlertDialog open={!!archiveTarget} onOpenChange={open => { if (!open && !busy) setArchiveTarget(undefined); }}>
                <AlertDialogContent>
                    <AlertDialogHeader>
                        <AlertDialogTitle>{t("app.archiveTitle")}</AlertDialogTitle>
                        <AlertDialogDescription>
                            {t("app.archiveText", { name: archiveTarget?.name ?? "" })}
                        </AlertDialogDescription>
                    </AlertDialogHeader>
                    <AlertDialogFooter>
                        <AlertDialogCancel disabled={busy}>{t("app.cancel")}</AlertDialogCancel>
                        <AlertDialogAction disabled={busy} onClick={event => { event.preventDefault(); if (archiveTarget) void setArchived(archiveTarget, true); }}>
                            {t("app.toArchive")}
                        </AlertDialogAction>
                    </AlertDialogFooter>
                </AlertDialogContent>
            </AlertDialog>
        </div>
    );
}
