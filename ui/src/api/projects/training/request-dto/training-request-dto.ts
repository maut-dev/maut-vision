/** Значение параметра обучения: число, строка, флаг, null («Авто») или список (classes, freeze, fraction, scale). */
export type TrainingParameterValue = number | string | boolean | null | (number | string)[];

/** Параметры: ключи протокола worker (см. training-parameters.ts); отсутствующий ключ — подобранное значение по умолчанию. */
export type TrainingParameters = Record<string, TrainingParameterValue>;

export type ListTrainingPresetsRequestDto = Record<string, never>;

/** «Сохранить как пресет…»: то же название (без учёта регистра) перезаписывает пресет. */
export interface SaveTrainingPresetRequestDto {
    name: string;
    parameters: TrainingParameters;
}

export interface DeleteTrainingPresetRequestDto {
    presetId: string;
}

/** refresh — опросить nvidia-smi заново (по умолчанию ответ кешируется). */
export interface ListGpusRequestDto {
    refresh?: boolean;
}

export type GetTrainingRuntimeRequestDto = Record<string, never>;

/** «Начать обучение»: operationId — новый на действие; повтор с тем же operationId вернёт тот же запуск. */
export interface StartTrainingRunRequestDto {
    projectId: string;
    datasetVersionId: string;
    parameters: TrainingParameters;
    presetName: string | null;
    operationId: string;
}

export interface CancelTrainingRunRequestDto {
    projectId: string;
    runId: string;
}

/** limit 1–200 (по умолчанию 50); before — id последнего запуска предыдущей страницы. */
export interface ListTrainingRunsRequestDto {
    projectId: string;
    limit?: number;
    before?: string;
}

export interface GetTrainingRunRequestDto {
    projectId: string;
    runId: string;
}

/** after — номер последней полученной эпохи; limit 1–1000 (по умолчанию 500). */
export interface ListTrainingEpochsRequestDto {
    projectId: string;
    runId: string;
    after?: number;
    limit?: number;
}

/** after — sequence последней полученной записи; limit 1–1000 (по умолчанию 200). */
export interface ListTrainingLogRequestDto {
    projectId: string;
    runId: string;
    after?: number;
    limit?: number;
}

/** Последние записи журнала, limit 1–1000 (по умолчанию 50). */
export interface TailTrainingLogRequestDto {
    projectId: string;
    runId: string;
    limit?: number;
}

export interface ListModelVersionsRequestDto {
    projectId: string;
}

export interface GetModelVersionRequestDto {
    projectId: string;
    versionId: string;
}

export interface SetWorkingModelVersionRequestDto {
    projectId: string;
    versionId: string;
}

/** Состав удаления версии для общего диалога удаления. */
export interface ReviewModelVersionDeletionRequestDto {
    projectId: string;
    versionId: string;
}

/** «Удалить версию»: рабочую, проверяемую или выгружаемую версию удалить нельзя (training.version_in_use, details.reason). */
export interface DeleteModelVersionRequestDto {
    projectId: string;
    versionId: string;
}

/** «Выгрузить ZIP», шаг 1: нативное «Сохранить как» с именем model-vN.zip. */
export interface SelectModelExportRequestDto {
    projectId: string;
    versionId: string;
}

/** «Выгрузить ZIP», шаг 2: destinationToken — из selectModelExportDestination; operationId — новый на действие. */
export interface StartModelExportRequestDto {
    projectId: string;
    versionId: string;
    destinationToken: string;
    operationId: string;
}
