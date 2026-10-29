export interface DeleteClassAssetRequestDto {
    projectId: string;
    assetId: string;
    /** The revision from the answer of the deletion review. */
    reviewRevision: string;
}
