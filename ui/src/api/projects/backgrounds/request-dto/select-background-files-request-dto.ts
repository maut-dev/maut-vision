/** Нативный диалог выбора файлов; заголовки файлов сверяются с размером набора setId. */
export interface SelectBackgroundFilesRequestDto {
    projectId: string;
    setId: string;
}
