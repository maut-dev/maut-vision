export interface SetVariantArchivedRequestDto {
    projectId: string;
    variantId: string;
    /** The revision of the entry the user saw; a mismatch gives *.revision_conflict. */
    revision: number;
    archived: boolean;
}
