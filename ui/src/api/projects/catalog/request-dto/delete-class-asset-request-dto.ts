export interface DeleteClassAssetRequestDto {
    projectId: string;
    assetId: string;
    /** Revision из ответа просмотра состава удаления. */
    reviewRevision: string;
}
