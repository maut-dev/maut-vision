export interface SetVariantArchivedRequestDto {
    projectId: string;
    variantId: string;
    /** Ревизия объекта, которую видел пользователь; при расхождении — *.revision_conflict. */
    revision: number;
    archived: boolean;
}
