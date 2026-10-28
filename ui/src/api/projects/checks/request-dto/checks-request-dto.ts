/** Часть датасета для оценки: val «Validation» | test «Test». */
export type EvaluationSplit = "val" | "test";

/**
 * «Проверить на датасете»: device — "cpu" или номер видеокарты (как в обучении: deviceOptions), batch 1–1024 (по умолчанию 16);
 * operationId — новый на действие (повтор вернёт ту же оценку).
 */
export interface StartModelEvaluationRequestDto {
    projectId: string;
    versionId: string;
    datasetVersionId: string;
    split: EvaluationSplit;
    device: string;
    batch: number | null;
    operationId: string;
}

export interface CancelModelEvaluationRequestDto {
    projectId: string;
    evaluationId: string;
}

export interface GetModelEvaluationRequestDto {
    projectId: string;
    evaluationId: string;
}

/** «Все проверки», новые первыми; limit 1–200 (по умолчанию 50), before — id последней оценки предыдущей страницы. */
export interface ListModelEvaluationsRequestDto {
    projectId: string;
    versionId?: string;
    datasetVersionId?: string;
    split?: EvaluationSplit;
    limit?: number;
    before?: string;
}

/** Матрица сравнения: последняя успешная оценка каждой версии на паре «датасет · часть» (без фильтров — все пары). */
export interface ListLatestModelEvaluationsRequestDto {
    projectId: string;
    datasetVersionId?: string;
    split?: EvaluationSplit;
}

export interface SelectCheckImageRequestDto {
    projectId: string;
}

export interface SelectCheckVideoRequestDto {
    projectId: string;
}

/** «Показать другой кадр»: кадр выбранного видео в timestampMs, уменьшенный до maxSide (по умолчанию 1600). */
export interface PreviewCheckVideoFrameRequestDto {
    projectId: string;
    selectionToken: string;
    timestampMs: number;
    maxSide?: number;
}

export type ListMonitorsRequestDto = Record<string, never>;

/** «Снять экран». */
export interface CaptureScreenRequestDto {
    projectId: string;
    monitor: number;
}

/** Общие поля: confidence — «Порог уверенности» (null — 0.25), maxDetections — null «Автоматически», device — "cpu" или номер видеокарты. */
export interface CheckParametersDto {
    confidence: number | null;
    maxDetections: number | null;
    device: string;
}

/** Рамка в пикселях кадра (у экрана — от угла монитора), размером со вход версии. */
export interface CheckRegionDto {
    x: number;
    y: number;
    width: number;
    height: number;
}

/** operationId — новый на действие; повтор того же запроса вернёт ту же проверку. */
export interface StartImageCheckRequestDto {
    projectId: string;
    versionId: string;
    selectionToken: string;
    parameters: CheckParametersDto;
    operationId: string;
}

/** stepMs — «Шаг» (null — 1000 мс); region — null, если видео не больше входа. */
export interface StartVideoCheckRequestDto {
    projectId: string;
    versionId: string;
    selectionToken: string;
    parameters: CheckParametersDto;
    stepMs: number | null;
    region: CheckRegionDto | null;
    operationId: string;
}

/** fps — «Кадров в секунду» (null — 3), maxDurationSec — «Остановить через» (null — 1800); region — null, если экран не больше входа. */
export interface StartScreenCheckRequestDto {
    projectId: string;
    versionId: string;
    monitor: number;
    parameters: CheckParametersDto;
    fps: number | null;
    maxDurationSec: number | null;
    region: CheckRegionDto | null;
    operationId: string;
}

export interface StopCheckRequestDto {
    projectId: string;
    checkId: string;
}

/** journalLimit 0–200 (по умолчанию 40): последняя страница журнала в recentFrames. */
export interface GetCheckRequestDto {
    projectId: string;
    checkId: string;
    journalLimit?: number;
}

export interface ListChecksRequestDto {
    projectId: string;
}

/** after — дочитать новые записи после ordinal; before — листать назад; без курсоров — последние limit (1–200, по умолчанию 100). */
export interface ListCheckJournalRequestDto {
    projectId: string;
    checkId: string;
    after?: number;
    before?: number;
    limit?: number;
}
