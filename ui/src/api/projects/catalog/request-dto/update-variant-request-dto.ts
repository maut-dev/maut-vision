export interface UpdateVariantRequestDto {
    projectId: string;
    variantId: string;
    /** Ревизия объекта, которую видел пользователь; при расхождении — *.revision_conflict. */
    revision: number;
    name: string;
    description: string;
}
