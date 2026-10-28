export interface SetClassVariantColorRequestDto {
    projectId: string;
    classVariantId: string;
    /** Ревизия объекта, которую видел пользователь; при расхождении — *.revision_conflict. */
    revision: number;
    /** #rrggbb или null — цвет по умолчанию. */
    color: string | null;
}
