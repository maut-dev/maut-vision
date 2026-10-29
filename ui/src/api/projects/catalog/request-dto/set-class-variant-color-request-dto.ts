export interface SetClassVariantColorRequestDto {
    projectId: string;
    classVariantId: string;
    /** The revision of the entry the user saw; a mismatch gives *.revision_conflict. */
    revision: number;
    /** #rrggbb, or null for the default color. */
    color: string | null;
}
