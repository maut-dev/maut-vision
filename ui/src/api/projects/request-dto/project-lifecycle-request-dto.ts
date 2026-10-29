/** Export…: opens Save As with the name "<name> <YYYY-MM-DD>.zip". */
export interface SelectProjectExportRequestDto {
    projectId: string;
    /** The file name in the language of the page when the project name has no characters a file name can keep ("Project"). */
    fallbackName?: string | null;
}

/** selectionToken — the answer of selectExportDestination; operationId — new for each click (a repeat returns the same job). */
export interface StartProjectExportRequestDto {
    projectId: string;
    operationId: string;
    selectionToken: string;
}

/** Import a project…: opens the choice of a .zip archive. */
export type SelectProjectArchiveRequestDto = Record<string, never>;

/** selectionToken — the answer of selectImportArchive; operationId — new for each click (a repeat returns the same import). */
export interface StartProjectImportRequestDto {
    operationId: string;
    selectionToken: string;
    /** The word of the suffix a copy gets when its name is taken: "import" → "Name (import)"; without it the host uses "import". */
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

/** revision — from reviewDeletion; cancelJobs — Cancel jobs and delete. */
export interface DeleteProjectRequestDto {
    projectId: string;
    revision: number;
    cancelJobs: boolean;
}
