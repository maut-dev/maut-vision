export interface UpdateVariantRequestDto {
    projectId: string;
    variantId: string;
    /** The revision of the entry the user saw; a mismatch gives *.revision_conflict. */
    revision: number;
    name: string;
    description: string;
}
