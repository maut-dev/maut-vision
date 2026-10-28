/** Запуски, новые первыми; limit 1–200 (по умолчанию 50), before — ID последнего запуска предыдущей страницы. */
export interface ListGenerationRunsRequestDto {
    projectId: string;
    limit?: number;
    before?: string;
}

export interface GetGenerationRunRequestDto {
    projectId: string;
    runId: string;
}

/**
 * «Сгенерировать N изображений»: recipeRevision — ревизия пресета, которую видел пользователь; seed — 0–4 294 967 295; count ≥ 1;
 * operationId — новый на каждое действие пользователя (повтор запроса с тем же operationId вернёт тот же запуск).
 */
export interface StartGenerationRunRequestDto {
    projectId: string;
    recipeId: string;
    recipeRevision: number;
    seed: number;
    count: number;
    operationId: string;
}

/** Удаляет завершённый запуск; изображения группы результата остаются. */
export interface DeleteGenerationRunRequestDto {
    projectId: string;
    runId: string;
}

/** «Настроить повтор». */
export interface GetGenerationRepeatDraftRequestDto {
    projectId: string;
    runId: string;
}
