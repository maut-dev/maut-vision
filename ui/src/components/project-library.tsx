import { useState } from "react";
import { Archive, ArchiveRestore, Boxes, Download, FolderOpen, Loader2, Plus, Search, Settings, Trash2, Upload } from "lucide-react";
import type { ProjectResponseDto } from "@/api/projects/response-dto/project-response-dto";
import { ProjectTransferBanner } from "@/components/project-transfer-banner";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { transferKey, type ProjectTransfersState } from "@/lib/use-project-transfers";
import type { ProjectsState } from "@/lib/use-projects";

/**
 * Библиотека: последний открытый проект стоит первым с отметкой «Последний открытый»; перенос (экспорт, импорт) — баннеры с ходом и
 * итогом; удаление — диалог владельца (remove). opening — проект, который сейчас открывается: другой можно открыть, не дожидаясь.
 */
export function ProjectLibrary({ library, transfers, busy, opening, notice, dismissNotice, create, edit, open, archive, remove, catalog }: {
    library: ProjectsState;
    transfers: ProjectTransfersState;
    busy: boolean;
    opening: string | undefined;
    /** Сообщение после удаления (часть файлов занята). */
    notice: string | undefined;
    dismissNotice: () => void;
    create: () => void;
    edit: (project: ProjectResponseDto) => void;
    open: (project: ProjectResponseDto) => void;
    archive: (project: ProjectResponseDto) => void;
    remove: (project: ProjectResponseDto) => void;
    catalog: () => void;
}) {
    const [search, setSearch] = useState("");
    const [showArchive, setShowArchive] = useState(false);
    const query = search.trim().toLocaleLowerCase();
    const last = library.lastProjectId;
    const projects = library.projects
        .filter(project => (showArchive || !project.archivedAt) && project.name.toLocaleLowerCase().includes(query))
        .sort((a, b) => Number(b.id === last) - Number(a.id === last));
    const archivedCount = library.projects.filter(project => project.archivedAt).length;
    return (
        <div className="space-y-6">
            <div className="flex flex-wrap items-start justify-between gap-4">
                <div>
                    <h1 className="text-2xl font-semibold">Проекты</h1>
                    <p className="mt-2 text-sm text-muted-foreground">Выберите задачу, чтобы открыть её материалы и настройки.</p>
                </div>
                <div className="flex flex-wrap gap-2">
                    <Button variant="outline" onClick={catalog} disabled={busy}><Boxes />Базовые модели</Button>
                    <Button
                        variant="outline"
                        onClick={() => void transfers.startImport()}
                        disabled={transfers.importing}
                        title="Добавить проект из архива, сохранённого на другом компьютере"
                        data-testid="import-project"
                    >
                        <Upload />Импортировать проект…
                    </Button>
                    <Button onClick={create} disabled={busy} data-testid="create-project"><Plus />Создать проект</Button>
                </div>
            </div>
            {library.error && (
                <Alert variant="destructive">
                    <AlertDescription>
                        <p>{library.error}</p>
                        <Button type="button" variant="outline" size="sm" className="mt-2" onClick={() => void library.refresh()}>Повторить загрузку</Button>
                    </AlertDescription>
                </Alert>
            )}
            {transfers.error && (
                <Alert variant="destructive" data-testid="transfer-error">
                    <AlertDescription>
                        <p>{transfers.error}</p>
                        <Button type="button" variant="ghost" size="sm" className="mt-2" onClick={transfers.clearError}>Скрыть</Button>
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
                        <Button type="button" variant="ghost" size="sm" className="mt-2" onClick={dismissNotice}>Скрыть</Button>
                    </AlertDescription>
                </Alert>
            )}
            <div className="flex flex-wrap items-center justify-between gap-4">
                <div className="relative w-full max-w-sm">
                    <Search className="absolute left-3 top-2.5 size-4 text-muted-foreground" aria-hidden />
                    <Input aria-label="Поиск проектов" placeholder="Поиск проектов" className="pl-9" value={search} onChange={event => setSearch(event.target.value)} />
                </div>
                <label className="flex items-center gap-2 text-sm">
                    <Checkbox checked={showArchive} onCheckedChange={value => setShowArchive(value === true)} />
                    Показать архив{archivedCount > 0 && ` (${archivedCount})`}
                </label>
            </div>
            <div className="overflow-hidden rounded-lg border">
                {library.loading && !library.projects.length ? (
                    <div className="flex items-center gap-2 p-6 text-muted-foreground" role="status">
                        <Loader2 className="size-4 animate-spin" aria-hidden />Загружаем проекты…
                    </div>
                ) : (
                    <div className="divide-y" data-testid="project-list">
                        {projects.map(project => (
                            <div key={project.id} data-project-id={project.id} className="flex flex-wrap items-center justify-between gap-4 p-5">
                                <div className="min-w-0 flex-1">
                                    <div className="flex items-center gap-2">
                                        <FolderOpen className="size-4 shrink-0 text-muted-foreground" aria-hidden />
                                        <h2 className="break-words font-medium">{project.name}</h2>
                                        {project.id === last && <Badge variant="secondary" data-testid="project-last">Последний открытый</Badge>}
                                        {project.archivedAt && <Badge variant="outline">В архиве</Badge>}
                                    </div>
                                    <p className="mt-2 max-w-prose whitespace-pre-wrap break-words text-sm text-muted-foreground">
                                        {project.description || "Без описания"}
                                    </p>
                                    <p className="mt-2 text-xs tabular-nums text-muted-foreground">
                                        {project.baseModelId} · {project.inputSize.width} × {project.inputSize.height} px
                                    </p>
                                </div>
                                <div className="flex flex-wrap gap-2">
                                    {!project.archivedAt && (
                                        <>
                                            <Button onClick={() => open(project)} disabled={busy || opening === project.id} data-testid="open-project">
                                                {opening === project.id ? <><Loader2 className="animate-spin" aria-hidden />Открытие…</> : "Открыть"}
                                            </Button>
                                            <Button variant="outline" onClick={() => edit(project)} disabled={busy}><Settings />Настройки</Button>
                                        </>
                                    )}
                                    <Button
                                        variant="outline"
                                        onClick={() => void transfers.startExport(project)}
                                        disabled={transfers.exporting.has(project.id)}
                                        title="Сохранить проект со всеми материалами, датасетами и весами моделей в один файл"
                                        data-testid="export-project"
                                    >
                                        <Download />Экспортировать…
                                    </Button>
                                    <Button variant="ghost" onClick={() => archive(project)} disabled={busy}>
                                        {project.archivedAt ? <><ArchiveRestore />Восстановить</> : <><Archive />В архив</>}
                                    </Button>
                                    <Button
                                        variant="ghost"
                                        className="text-destructive hover:text-destructive"
                                        onClick={() => remove(project)}
                                        disabled={busy || transfers.exporting.has(project.id)}
                                        aria-label={`Удалить проект ${project.name}`}
                                        data-testid="delete-project"
                                    >
                                        <Trash2 />Удалить…
                                    </Button>
                                </div>
                            </div>
                        ))}
                    </div>
                )}
                {!library.loading && projects.length === 0 && (
                    <div className="flex min-h-64 flex-col items-center justify-center gap-3 p-6 text-center">
                        <img src="./brand/logo-mark.png" alt="" className="size-12 opacity-80" draggable={false} />
                        <h2 className="font-medium">{library.projects.length ? "Проекты не найдены" : "Создайте первый проект"}</h2>
                        <p className="max-w-sm text-sm text-muted-foreground">
                            {library.projects.length
                                ? "Измените поиск или включите показ архива."
                                : "Проект объединяет классы, варианты объектов, изображения и датасеты одной задачи. Для него нужна установленная базовая модель."}
                        </p>
                        {!library.projects.length && (
                            <div className="flex flex-wrap justify-center gap-2">
                                <Button variant="outline" onClick={catalog}><Boxes />Базовые модели</Button>
                                <Button variant="outline" onClick={create}><Plus />Создать проект</Button>
                            </div>
                        )}
                    </div>
                )}
            </div>
        </div>
    );
}
