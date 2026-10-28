/** «Экспортировать…»: открывает «Сохранить как» с именем «<имя> <YYYY-MM-DD>.zip». */
export interface SelectProjectExportRequestDto {
    projectId: string;
    /** The file name in the language of the page when the project name has no characters a file name can keep («Проект»). */
    fallbackName?: string | null;
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
    /** The word of the suffix a copy gets when its name is taken: «импорт» → «Имя (импорт)»; without it the host uses "import". */
    copyLabel?: string | null;
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
