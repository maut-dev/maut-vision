import { isJobResponseDto } from "../jobs/response-dto/job-response-dto";
import { nativeBridge } from "../transport/native-bridge";
import type { CreateProjectRequestDto } from "./request-dto/create-project-request-dto";
import type { ListProjectsRequestDto } from "./request-dto/list-projects-request-dto";
import type { OpenProjectRequestDto } from "./request-dto/open-project-request-dto";
import type {
    CancelProjectImportRequestDto, DeleteProjectRequestDto, GetProjectImportRequestDto, ListProjectImportsRequestDto, ReviewProjectDeletionRequestDto,
    SelectProjectArchiveRequestDto, SelectProjectExportRequestDto, StartProjectExportRequestDto, StartProjectImportRequestDto,
} from "./request-dto/project-lifecycle-request-dto";
import type { SetProjectArchivedRequestDto } from "./request-dto/set-project-archived-request-dto";
import type { UpdateProjectRequestDto } from "./request-dto/update-project-request-dto";
import { isProjectLibraryResponseDto } from "./response-dto/list-projects-response-dto";
import {
    isProjectArchiveSelectionResponseDto, isProjectDeletionResponseDto, isProjectDeletionReviewResponseDto, isProjectExportSelectionResponseDto,
    isProjectImportListResponseDto, isProjectImportResponseDto,
} from "./response-dto/project-lifecycle-response-dto";
import { isProjectResponseDto } from "./response-dto/project-response-dto";

/**
 * Проекты библиотеки. Экспорт — задача projects.export (ход — job.changed); импорт — фоновая операция с событиями projectImport.changed;
 * удаление — reviewDeletion, затем delete с ревизией (событие project.removed).
 */
export const projectsApi = {
    get available(): boolean { return nativeBridge.available; },
    list(signal?: AbortSignal) {
        const request: ListProjectsRequestDto = {};
        return nativeBridge.request("projects.list", request, isProjectLibraryResponseDto, signal);
    },
    create(request: CreateProjectRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("projects.create", request, isProjectResponseDto, signal);
    },
    update(request: UpdateProjectRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("projects.update", request, isProjectResponseDto, signal);
    },
    setArchived(request: SetProjectArchivedRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("projects.setArchived", request, isProjectResponseDto, signal);
    },
    open(request: OpenProjectRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("projects.open", request, isProjectResponseDto, signal);
    },
    selectExportDestination(request: SelectProjectExportRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("projects.selectExportDestination", request, isProjectExportSelectionResponseDto, signal);
    },
    /** Ставит projects.export и сразу возвращает снимок задачи; ход и итог — job.changed и parseProjectExportResult. */
    startExport(request: StartProjectExportRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("projects.startExport", request, isJobResponseDto, signal);
    },
    selectImportArchive(signal?: AbortSignal) {
        const request: SelectProjectArchiveRequestDto = {};
        return nativeBridge.request("projects.selectImportArchive", request, isProjectArchiveSelectionResponseDto, signal);
    },
    startImport(request: StartProjectImportRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("projects.startImport", request, isProjectImportResponseDto, signal);
    },
    getImport(request: GetProjectImportRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("projects.getImport", request, isProjectImportResponseDto, signal);
    },
    listImports(signal?: AbortSignal) {
        const request: ListProjectImportsRequestDto = {};
        return nativeBridge.request("projects.listImports", request, isProjectImportListResponseDto, signal);
    },
    cancelImport(request: CancelProjectImportRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("projects.cancelImport", request, isProjectImportResponseDto, signal);
    },
    reviewDeletion(request: ReviewProjectDeletionRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("projects.reviewDeletion", request, isProjectDeletionReviewResponseDto, signal);
    },
    delete(request: DeleteProjectRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("projects.delete", request, isProjectDeletionResponseDto, signal);
    },
};
