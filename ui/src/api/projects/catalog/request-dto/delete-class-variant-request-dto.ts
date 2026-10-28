export interface DeleteClassVariantRequestDto {
    projectId: string;
    classVariantId: string;
    /** Revision из ответа просмотра состава удаления. */
    reviewRevision: string;
}
