export interface DeleteClassRequestDto {
    projectId: string;
    classId: string;
    /** Revision из ответа просмотра состава удаления. */
    reviewRevision: string;
}
