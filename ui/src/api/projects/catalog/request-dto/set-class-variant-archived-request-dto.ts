export interface SetClassVariantArchivedRequestDto {
    projectId: string;
    classVariantId: string;
    /** The revision of the entry the user saw; a mismatch gives *.revision_conflict. */
    revision: number;
    archived: boolean;
}
