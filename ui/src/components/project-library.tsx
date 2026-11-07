import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Archive, ArchiveRestore, Boxes, Download, FolderOpen, Loader2, Plus, Search, Settings, Trash2, Upload } from "lucide-react";
import type { ProjectResponseDto } from "@/api/projects/response-dto/project-response-dto";
import { ProjectTransferBanner } from "@/components/project-transfer-banner";
import { Notice } from "@/components/layout/notice";
import { Toolbar } from "@/components/layout/toolbar";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { locale } from "@/lib/format";
import { transferKey, type ProjectTransfersState } from "@/lib/use-project-transfers";
import type { ProjectsState } from "@/lib/use-projects";

/**
 * The library: the last opened project comes first, marked Last opened; transfers (export, import) are banners with progress and an
 * outcome; deletion is the dialog of the owner (remove). opening — the project being opened now: another one can open without waiting.
 * "Create a project" is the one brand action of the screen; below lg the row actions keep only their icons, named by aria-label and title.
 */
export function ProjectLibrary({ library, transfers, busy, opening, notice, dismissNotice, create, edit, open, archive, remove, catalog }: {
    library: ProjectsState;
    transfers: ProjectTransfersState;
    busy: boolean;
    opening: string | undefined;
    /** The message after a deletion (some files are busy). */
    notice: string | undefined;
    dismissNotice: () => void;
    create: () => void;
    edit: (project: ProjectResponseDto) => void;
    open: (project: ProjectResponseDto) => void;
    archive: (project: ProjectResponseDto) => void;
    remove: (project: ProjectResponseDto) => void;
    catalog: () => void;
}) {
    const { t } = useTranslation();
    const [search, setSearch] = useState("");
    const [showArchive, setShowArchive] = useState(false);
    const query = search.trim().toLocaleLowerCase(locale());
    const last = library.lastProjectId;
    const projects = library.projects
        .filter(project => (showArchive || !project.archivedAt) && project.name.toLocaleLowerCase(locale()).includes(query))
        .sort((a, b) => Number(b.id === last) - Number(a.id === last));
    const archivedCount = library.projects.filter(project => project.archivedAt).length;
    return (
        <div className="space-y-6">
            <div className="flex flex-wrap items-start justify-between gap-4">
                <div>
                    <h1 className="text-2xl font-semibold tracking-[-0.025em]">{t("library.title")}</h1>
                    <p className="mt-2 text-sm text-text-2">{t("library.subtitle")}</p>
                </div>
                <div className="flex flex-wrap gap-2">
                    <Button variant="outline" onClick={catalog} disabled={busy}><Boxes />{t("app.baseModels")}</Button>
                    <Button
                        variant="outline"
                        onClick={() => void transfers.startImport()}
                        disabled={transfers.importing}
                        title={t("library.importHint")}
                        data-testid="import-project"
                    >
                        <Upload />{t("library.import")}
                    </Button>
                    <Button variant="brand" onClick={create} disabled={busy} data-testid="create-project"><Plus />{t("library.create")}</Button>
                </div>
            </div>
            {library.error && (
                <Notice variant="destructive" action={{ label: t("common.retryLoading"), onClick: () => void library.refresh() }}>{library.error}</Notice>
            )}
            {transfers.error && (
                <Notice variant="destructive" data-testid="transfer-error" onDismiss={transfers.clearError}>{transfers.error}</Notice>
            )}
            {transfers.transfers.map(transfer => (
                <ProjectTransferBanner
                    key={transferKey(transfer)}
                    transfer={transfer}
                    cancel={() => void transfers.cancel(transfer)}
                    dismiss={() => transfers.dismiss(transfer)}
                />
            ))}
            {notice && (
                <Notice data-testid="project-deletion-notice" onDismiss={dismissNotice}><p>{notice}</p></Notice>
            )}
            <div className="flex flex-wrap items-center justify-between gap-4">
                <div className="relative w-full max-w-sm">
                    <Search className="absolute top-2.5 left-3 size-4 text-text-3" aria-hidden />
                    <Input aria-label={t("library.search")} placeholder={t("library.search")} className="pl-9" value={search} onChange={event => setSearch(event.target.value)} />
                </div>
                <label className="flex cursor-pointer items-center gap-2 text-[13px] text-text-2">
                    <Checkbox checked={showArchive} onCheckedChange={value => setShowArchive(value === true)} />
                    {t("library.showArchive")}{archivedCount > 0 && ` (${archivedCount})`}
                </label>
            </div>
            <div>
                {library.loading && !library.projects.length ? (
                    <div className="surface flex items-center gap-2 p-6 text-text-2" role="status">
                        <Loader2 className="size-4 animate-spin" aria-hidden />{t("library.loading")}
                    </div>
                ) : (
                    <ul className="space-y-2" data-testid="project-list">
                        {projects.map(project => (
                            <li key={project.id} data-project-id={project.id} data-selected={project.id === last || undefined}
                                className="surface corners flex flex-wrap items-center justify-between gap-x-4 gap-y-3 px-4 py-3.5 transition-colors duration-120 ease-glass hover:bg-ink-2">
                                <div className="min-w-0 flex-1 basis-64">
                                    <div className="flex flex-wrap items-center gap-2">
                                        <FolderOpen className="size-4 shrink-0 text-text-3" aria-hidden />
                                        <h2 className="min-w-0 text-[15px] font-semibold tracking-[-0.015em] break-words">{project.name}</h2>
                                        {project.id === last && <Badge variant="brand" data-testid="project-last">{t("library.lastOpened")}</Badge>}
                                        {project.archivedAt && <Badge variant="plain">{t("library.archived")}</Badge>}
                                    </div>
                                    <p className="mt-1 line-clamp-2 max-w-prose text-[13px] break-words whitespace-pre-wrap text-text-2" title={project.description || undefined}>
                                        {project.description || t("library.noDescription")}
                                    </p>
                                    <p className="mt-1 text-xs tabular-nums text-text-3">
                                        {project.baseModelId} · {project.inputSize.width} × {project.inputSize.height} px
                                    </p>
                                </div>
                                {/* Below lg the secondary actions keep only their icons, so a row stays compact; "Open" keeps its word. */}
                                <Toolbar className="gap-1">
                                    {!project.archivedAt && (
                                        <>
                                            <Button size="sm" onClick={() => open(project)} disabled={busy || opening === project.id} data-testid="open-project">
                                                {opening === project.id ? <><Loader2 className="animate-spin" aria-hidden />{t("library.opening")}</> : t("library.open")}
                                            </Button>
                                            <Button size="sm" variant="ghost" aria-label={t("library.settings")} title={t("library.settings")} onClick={() => edit(project)} disabled={busy}>
                                                <Settings /><span className="hidden lg:inline">{t("library.settings")}</span>
                                            </Button>
                                        </>
                                    )}
                                    <Button
                                        size="sm"
                                        variant="ghost"
                                        onClick={() => void transfers.startExport(project)}
                                        disabled={transfers.exporting.has(project.id)}
                                        aria-label={t("library.export")}
                                        title={t("library.exportHint")}
                                        data-testid="export-project"
                                    >
                                        <Download /><span className="hidden lg:inline">{t("library.export")}</span>
                                    </Button>
                                    <Button size="sm" variant="ghost" onClick={() => archive(project)} disabled={busy}
                                        aria-label={project.archivedAt ? t("library.restore") : t("app.toArchive")} title={project.archivedAt ? t("library.restore") : t("app.toArchive")}>
                                        {project.archivedAt
                                            ? <><ArchiveRestore /><span className="hidden lg:inline">{t("library.restore")}</span></>
                                            : <><Archive /><span className="hidden lg:inline">{t("app.toArchive")}</span></>}
                                    </Button>
                                    <Button
                                        size="sm"
                                        variant="ghost"
                                        className="text-danger hover:text-danger"
                                        onClick={() => remove(project)}
                                        disabled={busy || transfers.exporting.has(project.id)}
                                        aria-label={t("library.deleteLabel", { name: project.name })}
                                        title={t("library.delete")}
                                        data-testid="delete-project"
                                    >
                                        <Trash2 /><span className="hidden lg:inline">{t("library.delete")}</span>
                                    </Button>
                                </Toolbar>
                            </li>
                        ))}
                    </ul>
                )}
                {!library.loading && projects.length === 0 && (
                    <div className="surface flex min-h-64 flex-col items-center justify-center gap-3 p-6 text-center">
                        <img src="./brand/logo-mark.png" alt="" className="size-12 opacity-80" draggable={false} />
                        <h2 className="text-[15px] font-semibold">{library.projects.length ? t("library.notFound") : t("library.emptyTitle")}</h2>
                        <p className="max-w-sm text-sm text-text-2">
                            {library.projects.length ? t("library.notFoundHint") : t("library.emptyText")}
                        </p>
                        {!library.projects.length && (
                            <div className="flex flex-wrap justify-center gap-2">
                                <Button variant="outline" onClick={catalog}><Boxes />{t("app.baseModels")}</Button>
                                <Button variant="outline" onClick={create}><Plus />{t("library.create")}</Button>
                            </div>
                        )}
                    </div>
                )}
            </div>
        </div>
    );
}
