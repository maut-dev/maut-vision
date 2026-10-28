/** Кадр файла выбора для рамки. */
export interface PreviewBackgroundSourceRequestDto {
    projectId: string;
    /** Токен из backgrounds.selectFiles; превью не привязывает его к операции. */
    selectionToken: string;
    /** index файла из ответа selectFiles. */
    sourceIndex: number;
    /** 64–4096, по умолчанию 1600. */
    maxSide?: number;
}
