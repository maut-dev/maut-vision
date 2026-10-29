export interface ImportClassAssetsRequestDto {
    projectId: string;
    classVariantId: string;
    /** A token from classes.selectAssetFiles; valid for 30 minutes and on this page only. */
    selectionToken: string;
    /** Created by the UI for one action; a repeat with the same value returns the same job. */
    operationId: string;
}
