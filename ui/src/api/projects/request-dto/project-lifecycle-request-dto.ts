/** «Экспортировать…»: открывает «Сохранить как» с именем «<имя> <YYYY-MM-DD>.zip». */
export interface SelectProjectExportRequestDto {
    projectId: string;
}

/** selectionToken — ответ selectExportDestination; operationId — новый на каждое нажатие (повтор вернёт ту же задачу). */
export interface StartProjectExportRequestDto {
    projectId: string;
    operationId: string;
    selectionToken: string;
}

/** «Импортировать проект…»: открывает выбор архива .zip. */
export type SelectProjectArchiveRequestDto = Record<string, never>;

/** selectionToken — ответ selectImportArchive; operationId — новый на каждое нажатие (повтор вернёт тот же импорт). */
export interface StartProjectImportRequestDto {
    operationId: string;
    selectionToken: string;
}

export interface GetProjectImportRequestDto {
    importId: string;
}

export type ListProjectImportsRequestDto = Record<string, never>;

export interface CancelProjectImportRequestDto {
    importId: string;
}

export interface ReviewProjectDeletionRequestDto {
    projectId: string;
}

/** revision — из reviewDeletion; cancelJobs — «Отменить задачи и удалить». */
export interface DeleteProjectRequestDto {
    projectId: string;
    revision: number;
    cancelJobs: boolean;
}
