import { useCallback, useEffect, useRef, useState } from "react";
import { subscribeJobChanged, subscribeProjectImportChanged } from "@/api/events/subscriptions";
import { jobsApi } from "@/api/jobs/jobs-api";
import type { JobResponseDto } from "@/api/jobs/response-dto/job-response-dto";
import { projectsApi } from "@/api/projects/projects-api";
import {
    parseProjectExportResult,
    projectExportJobKind,
    type ProjectExportResultDto,
    type ProjectImportResponseDto,
} from "@/api/projects/response-dto/project-lifecycle-response-dto";
import type { ProjectResponseDto } from "@/api/projects/response-dto/project-response-dto";
import { i18n } from "@/i18n";
import { errorMessage } from "./format";
import { isJobActive } from "./use-asset-imports";

/** Экспорт проекта: задача projects.export и итог (после отмены или ошибки — сколько успели). */
export interface ProjectExportTransfer {
    kind: "export";
    projectId: string;
    projectName: string;
    fileName: string | null;
    job: JobResponseDto;
    result: ProjectExportResultDto | null;
}

export interface ProjectImportTransfer {
    kind: "import";
    operation: ProjectImportResponseDto;
}

export type ProjectTransfer = ProjectExportTransfer | ProjectImportTransfer;

export const transferKey = (transfer: ProjectTransfer) => transfer.kind === "export" ? transfer.job.id : transfer.operation.id;

export const isTransferRunning = (transfer: ProjectTransfer) =>
    transfer.kind === "export" ? isJobActive(transfer.job) : transfer.operation.status === "running";

/**
 * Перенос проектов из библиотеки. Экспорт — «Сохранить как» (закрытое окно ничего не запускает), затем задача projects.export с
 * ходом по job.changed; импорт — выбор архива, затем фоновая операция с ходом по projectImport.changed. Опроса нет: после
 * перезагрузки страницы идущий импорт берётся из listImports, идущий экспорт — из активных задач проектов, хранилище которых
 * подготовлено. imported вызывается, когда на глазах страницы импорт завершился успешно.
 */
export function useProjectTransfers(projects: readonly ProjectResponseDto[], loaded: boolean, imported: (projectId: string) => void) {
    const [exports, setExports] = useState<readonly ProjectExportTransfer[]>([]);
    const [importState, setImportState] = useState<ProjectImportTransfer>();
    const [error, setError] = useState<string>();
    const names = useRef(new Map<string, string>());
    names.current = new Map(projects.map(project => [project.id, project.name]));
    const onImported = useRef(imported);
    onImported.current = imported;
    // Импорты, которые страница видела идущими: только их успех открывает проект.
    const watched = useRef(new Set<string>());
    const dismissed = useRef(new Set<string>());
    const seenJobs = useRef(new Map<string, JobResponseDto>());
    const seenImports = useRef(new Map<string, ProjectImportResponseDto>());
    const mounted = useRef(true);

    const applyJob = useCallback((job: JobResponseDto, start?: { projectName: string; fileName: string | null }) => {
        if (job.kind !== projectExportJobKind) return;
        // Снимки, пришедшие раньше ответа старта, не теряются: применяется последний по sequence.
        const known = seenJobs.current.get(job.id);
        const latest = known && known.sequence >= job.sequence ? known : job;
        seenJobs.current.set(job.id, latest);
        if (dismissed.current.has(job.id)) return;
        setExports(items => {
            const current = items.find(item => item.job.id === job.id);
            if (current && current.job.sequence >= latest.sequence && !start) return items;
            // Задача, о которой страница не знала (поставлена до перезагрузки), показывается, только пока идёт.
            if (!current && !start && !isJobActive(latest)) return items;
            const next: ProjectExportTransfer = {
                kind: "export",
                projectId: latest.projectId,
                projectName: start?.projectName ?? current?.projectName ?? names.current.get(latest.projectId) ?? latest.label?.text ?? "",
                fileName: start?.fileName ?? current?.fileName ?? null,
                job: latest,
                result: parseProjectExportResult(latest),
            };
            return current ? items.map(item => item === current ? next : item) : [...items, next];
        });
    }, []);

    const applyImport = useCallback((operation: ProjectImportResponseDto, own = false) => {
        const known = seenImports.current.get(operation.id);
        const latest = known && known.sequence >= operation.sequence ? known : operation;
        seenImports.current.set(operation.id, latest);
        if (own || latest.status === "running") watched.current.add(latest.id);
        if (latest.status === "succeeded" && latest.projectId && watched.current.delete(latest.id)) onImported.current(latest.projectId);
        if (dismissed.current.has(latest.id)) return;
        setImportState(current => {
            if (current && current.operation.id === latest.id && current.operation.sequence >= latest.sequence) return current;
            // Одновременно идёт один импорт: завершённый чужой снимок не заменяет идущий.
            if (current && current.operation.id !== latest.id && current.operation.status === "running" && latest.status !== "running") return current;
            return { kind: "import", operation: latest };
        });
    }, []);

    useEffect(() => {
        mounted.current = true;
        const unsubscribeJobs = subscribeJobChanged(event => applyJob(event.data));
        const unsubscribeImports = subscribeProjectImportChanged(event => applyImport(event.data));
        // Идущий импорт переживает перезагрузку страницы; завершённые не показываются заново.
        projectsApi.listImports().then(
            items => items.filter(item => item.status === "running").forEach(item => applyImport(item)),
            () => { /* без списка импорт появится со следующим событием */ });
        return () => {
            mounted.current = false;
            unsubscribeJobs();
            unsubscribeImports();
        };
    }, [applyJob, applyImport]);

    // Идущий экспорт после перезагрузки: задачи видны только у проектов с подготовленным хранилищем, остальные отвечают отказом.
    const reconnected = useRef(false);
    useEffect(() => {
        if (!loaded || reconnected.current) return;
        reconnected.current = true;
        for (const project of projects) {
            jobsApi.listActive({ projectId: project.id, limit: 5 }).then(
                page => page.items.forEach(item => applyJob(item.job)),
                () => { /* проект не подготовлен — экспорта в нём нет */ });
        }
    }, [loaded, projects, applyJob]);

    /** «Экспортировать…»: false — окно «Сохранить как» закрыто. */
    const startExport = useCallback(async (project: ProjectResponseDto) => {
        setError(undefined);
        try {
            const destination = await projectsApi.selectExportDestination({ projectId: project.id, fallbackName: i18n.t("transfer.fallbackName") });
            if (!destination.selectionToken) return;
            const job = await projectsApi.startExport({ projectId: project.id, operationId: crypto.randomUUID(), selectionToken: destination.selectionToken });
            if (mounted.current) applyJob(job, { projectName: project.name, fileName: destination.fileName });
        } catch (cause) {
            if (mounted.current) setError(errorMessage(cause));
        }
    }, [applyJob]);

    /** «Импортировать проект…»: закрытое окно выбора ничего не запускает. */
    const startImport = useCallback(async () => {
        setError(undefined);
        try {
            const archive = await projectsApi.selectImportArchive();
            if (!archive.selectionToken) return;
            const operation = await projectsApi.startImport({
                operationId: crypto.randomUUID(), selectionToken: archive.selectionToken, copyLabel: i18n.t("transfer.copyLabel"),
            });
            if (mounted.current) applyImport(operation, true);
        } catch (cause) {
            if (mounted.current) setError(errorMessage(cause));
        }
    }, [applyImport]);

    const cancel = useCallback(async (transfer: ProjectTransfer) => {
        setError(undefined);
        try {
            if (transfer.kind === "export") applyJob(await jobsApi.cancel({ projectId: transfer.projectId, jobId: transfer.job.id }));
            else applyImport(await projectsApi.cancelImport({ importId: transfer.operation.id }));
        } catch (cause) {
            if (mounted.current) setError(errorMessage(cause));
        }
    }, [applyJob, applyImport]);

    const dismiss = useCallback((transfer: ProjectTransfer) => {
        const key = transferKey(transfer);
        dismissed.current.add(key);
        if (transfer.kind === "export") setExports(items => items.filter(item => item.job.id !== key));
        else setImportState(current => current?.operation.id === key ? undefined : current);
    }, []);

    const transfers: ProjectTransfer[] = importState ? [...exports, importState] : [...exports];
    const exporting = new Set(exports.filter(item => isJobActive(item.job)).map(item => item.projectId));
    const importing = importState?.operation.status === "running";
    return { transfers, exporting, importing, error, clearError: () => setError(undefined), startExport, startImport, cancel, dismiss };
}

export type ProjectTransfersState = ReturnType<typeof useProjectTransfers>;
