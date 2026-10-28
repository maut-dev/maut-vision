export interface ListClassAssetsRequestDto {
    projectId: string;
    classVariantId: string;
    /** 1–500, по умолчанию 100. */
    limit?: number;
    /** NextCursor предыдущей страницы. */
    after?: string;
}
