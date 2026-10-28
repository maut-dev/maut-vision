/** Рамка файла выбора: sourceIndex — index из selectFiles, left/top — левый верхний угол в пикселях файла. */
export interface BackgroundCropRequestDto {
    sourceIndex: number;
    left: number;
    top: number;
}

/** Одна рамка на всё видео: левый верхний угол в пикселях кадра. */
export interface BackgroundRegionRequestDto {
    left: number;
    top: number;
}

/** «Размывать фоны»: радиус от/до 0–1000 px; seed 0–4 294 967 295 или null («Из операции»). */
export interface BackgroundBlurRequestDto {
    minRadius: number;
    maxRadius: number;
    seed?: number | null;
}

/**
 * Запуск подготовки (backgrounds.import) и его примеры (backgrounds.previewImport) — одна форма: примеры с тем же operationId
 * совпадают с будущими фонами. import привязывает selectionToken к operationId (выбор действует на один запуск), previewImport — нет.
 *
 * «Готовые кадры»: без sourceIndices и crops — все файлы выбора, нужного размера берутся как есть, остальные пропускаются.
 * «Нарезать из изображений»: sourceIndices — включённые файлы по порядку (до 500), crops — рамки для файлов больше набора;
 * файл без рамки должен быть ровно размера набора.
 * «Нарезать из видео»: region, startMs, intervalMs и count («Сколько фонов нарезать», 1…availableVideoFrames) обязательны.
 */
export interface ImportBackgroundsRequestDto {
    projectId: string;
    setId: string;
    selectionToken: string;
    /** Создаётся UI на один запуск; повтор import с тем же значением вернёт ту же задачу. */
    operationId: string;
    sourceIndices?: number[];
    crops?: BackgroundCropRequestDto[];
    region?: BackgroundRegionRequestDto;
    /** Видео: «Начать с», мс. */
    startMs?: number;
    /** Видео: «Брать кадр раз в», мс (≥ 1). */
    intervalMs?: number;
    /** Видео — сколько фонов нарезать; файлы — необязательный предел добавленных. */
    count?: number;
    /** «Пропускать повторяющиеся кадры»: почти одинаковые (≤ 3/255) с набором и уже взятыми кадрами. По умолчанию false. */
    skipSimilar?: boolean;
    blur?: BackgroundBlurRequestDto | null;
}
