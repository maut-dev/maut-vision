export interface DeleteVariantRequestDto {
    projectId: string;
    variantId: string;
    /** Revision из ответа просмотра состава удаления. */
    reviewRevision: string;
}
