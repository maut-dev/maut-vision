import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Archive, ArchiveRestore, Boxes, Download, FolderOpen, Loader2, Plus, Search, Settings, Trash2, Upload } from "lucide-react";
import type { ProjectResponseDto } from "@/api/projects/response-dto/project-response-dto";
import { ProjectTransferBanner } from "@/components/project-transfer-banner";
import { Alert, AlertDescription } from "@/components/ui/alert";
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
                    <h1 className="text-2xl font-semibold">{t("library.title")}</h1>
                    <p className="mt-2 text-sm text-muted-foreground">{t("library.subtitle")}</p>
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
                    <Button onClick={create} disabled={busy} data-testid="create-project"><Plus />{t("library.create")}</Button>
                </div>
            </div>
            {library.error && (
                <Alert variant="destructive">
                    <AlertDescription>
                        <p>{library.error}</p>
                        <Button type="button" variant="outline" size="sm" className="mt-2" onClick={() => void library.refresh()}>{t("common.retryLoading")}</Button>
                    </AlertDescription>
                </Alert>
            )}
            {transfers.error && (
                <Alert variant="destructive" data-testid="transfer-error">
                    <AlertDescription>
                        <p>{transfers.error}</p>
                        <Button type="button" variant="ghost" size="sm" className="mt-2" onClick={transfers.clearError}>{t("common.hide")}</Button>
                    </AlertDescription>
                </Alert>
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
                <Alert role="status" data-testid="project-deletion-notice">
                    <AlertDescription>
                        <p>{notice}</p>
                        <Button type="button" variant="ghost" size="sm" className="mt-2" onClick={dismissNotice}>{t("common.hide")}</Button>
                    </AlertDescription>
                </Alert>
            )}
            <div className="flex flex-wrap items-center justify-between gap-4">
                <div className="relative w-full max-w-sm">
                    <Search className="absolute left-3 top-2.5 size-4 text-muted-foreground" aria-hidden />
                    <Input aria-label={t("library.search")} placeholder={t("library.search")} className="pl-9" value={search} onChange={event => setSearch(event.target.value)} />
                </div>
                <label className="flex items-center gap-2 text-sm">
                    <Checkbox checked={showArchive} onCheckedChange={value => setShowArchive(value === true)} />
                    {t("library.showArchive")}{archivedCount > 0 && ` (${archivedCount})`}
                </label>
            </div>
            <div className="overflow-hidden rounded-lg border">
                {library.loading && !library.projects.length ? (
                    <div className="flex items-center gap-2 p-6 text-muted-foreground" role="status">
                        <Loader2 className="size-4 animate-spin" aria-hidden />{t("library.loading")}
                    </div>
                ) : (
                    <div className="divide-y" data-testid="project-list">
                        {projects.map(project => (
                            <div key={project.id} data-project-id={project.id} className="flex flex-wrap items-center justify-between gap-4 p-5">
                                <div className="min-w-0 flex-1">
                                    <div className="flex items-center gap-2">
                                        <FolderOpen className="size-4 shrink-0 text-muted-foreground" aria-hidden />
                                        <h2 className="break-words font-medium">{project.name}</h2>
                                        {project.id === last && <Badge variant="secondary" data-testid="project-last">{t("library.lastOpened")}</Badge>}
                                        {project.archivedAt && <Badge variant="outline">{t("library.archived")}</Badge>}
                                    </div>
                                    <p className="mt-2 max-w-prose whitespace-pre-wrap break-words text-sm text-muted-foreground">
                                        {project.description || t("library.noDescription")}
                                    </p>
                                    <p className="mt-2 text-xs tabular-nums text-muted-foreground">
                                        {project.baseModelId} · {project.inputSize.width} × {project.inputSize.height} px
                                    </p>
                                </div>
                                <div className="flex flex-wrap gap-2">
                                    {!project.archivedAt && (
                                        <>
                                            <Button onClick={() => open(project)} disabled={busy || opening === project.id} data-testid="open-project">
                                                {opening === project.id ? <><Loader2 className="animate-spin" aria-hidden />{t("library.opening")}</> : t("library.open")}
                                            </Button>
                                            <Button variant="outline" onClick={() => edit(project)} disabled={busy}><Settings />{t("library.settings")}</Button>
                                        </>
                                    )}
                                    <Button
                                        variant="outline"
                                        onClick={() => void transfers.startExport(project)}
                                        disabled={transfers.exporting.has(project.id)}
                                        title={t("library.exportHint")}
                                        data-testid="export-project"
                                    >
                                        <Download />{t("library.export")}
                                    </Button>
                                    <Button variant="ghost" onClick={() => archive(project)} disabled={busy}>
                                        {project.archivedAt ? <><ArchiveRestore />{t("library.restore")}</> : <><Archive />{t("app.toArchive")}</>}
                                    </Button>
                                    <Button
                                        variant="ghost"
                                        className="text-destructive hover:text-destructive"
                                        onClick={() => remove(project)}
                                        disabled={busy || transfers.exporting.has(project.id)}
                                        aria-label={t("library.deleteLabel", { name: project.name })}
                                        data-testid="delete-project"
                                    >
                                        <Trash2 />{t("library.delete")}
                                    </Button>
                                </div>
                            </div>
                        ))}
                    </div>
                )}
                {!library.loading && projects.length === 0 && (
                    <div className="flex min-h-64 flex-col items-center justify-center gap-3 p-6 text-center">
                        <img src="./brand/logo-mark.png" alt="" className="size-12 opacity-80" draggable={false} />
                        <h2 className="font-medium">{library.projects.length ? t("library.notFound") : t("library.emptyTitle")}</h2>
                        <p className="max-w-sm text-sm text-muted-foreground">
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
