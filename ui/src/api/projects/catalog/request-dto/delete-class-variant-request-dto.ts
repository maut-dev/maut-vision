export interface DeleteClassVariantRequestDto {
    projectId: string;
    classVariantId: string;
    /** The revision from the answer of the deletion review. */
    reviewRevision: string;
}
