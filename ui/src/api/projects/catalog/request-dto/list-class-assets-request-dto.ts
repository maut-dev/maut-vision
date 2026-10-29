export interface ListClassAssetsRequestDto {
    projectId: string;
    classVariantId: string;
    /** 1–500, 100 by default. */
    limit?: number;
    /** NextCursor of the previous page. */
    after?: string;
}
