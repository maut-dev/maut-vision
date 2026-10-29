export interface DeleteVariantRequestDto {
    projectId: string;
    variantId: string;
    /** The revision from the answer of the deletion review. */
    reviewRevision: string;
}
