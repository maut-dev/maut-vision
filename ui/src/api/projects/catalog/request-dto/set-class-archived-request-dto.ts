export interface SetClassArchivedRequestDto {
    projectId: string;
    classId: string;
    /** Ревизия объекта, которую видел пользователь; при расхождении — *.revision_conflict. */
    revision: number;
    archived: boolean;
}
