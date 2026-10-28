export interface ImportClassAssetsRequestDto {
    projectId: string;
    classVariantId: string;
    /** Токен из classes.selectAssetFiles; действует 30 минут и только на этой странице. */
    selectionToken: string;
    /** Создаётся UI на одно действие; повтор с тем же значением вернёт ту же задачу. */
    operationId: string;
}
