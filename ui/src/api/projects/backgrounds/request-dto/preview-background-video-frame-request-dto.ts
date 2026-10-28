/** Кадр выбранного видео для рамки: «кадр-пример в середине» и «Показать другой кадр». */
export interface PreviewBackgroundVideoFrameRequestDto {
    projectId: string;
    /** Токен из backgrounds.selectVideo. */
    selectionToken: string;
    /** Отметка ≥ 0 и раньше конца видео; берётся первый кадр не раньше неё. */
    timestampMs: number;
    /** 64–4096, по умолчанию 1600. */
    maxSide?: number;
}
