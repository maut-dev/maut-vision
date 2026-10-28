export interface SetClassVariantArchivedRequestDto {
    projectId: string;
    classVariantId: string;
    /** Ревизия объекта, которую видел пользователь; при расхождении — *.revision_conflict. */
    revision: number;
    archived: boolean;
}
