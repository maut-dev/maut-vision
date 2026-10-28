/** Роль объекта без варианта класса: «Объект», «Негатив», «Отказ» (план отклоняется с datasets.unassigned_variant). */
export type DatasetUnassignedVariantRole = "target" | "negative" | "reject";

/** Роль объекта класса, не выбранного для обучения: «Негатив» или «Отказ» (datasets.unselected_class). */
export type DatasetUnselectedClassRole = "negative" | "reject";

/** Часть датасета — папки YOLO. В UI: «Train», «Validation», «Test». */
export type DatasetSplit = "train" | "val" | "test";

/** Вид операции датасета: сборка, проверка целостности, экспорт ZIP. */
export type DatasetOperationKind = "build" | "verify" | "export";

/**
 * Спецификация сборки («Подготовить сборку»). groupIds — «Группы изображений»; classIds — «Классы для обучения» (индексы 0..n−1
 * назначит host по порядку каталога, не по порядку в списке); targetVariantIds / negativeVariantIds — варианты классов (привязки),
 * обычно целевые — доступные варианты выбранных классов, негативные — архивные (datasetVariantRoles). valRatio и testRatio — доли
 * 0..1 (не проценты): valRatio > 0, testRatio ≥ 0, сумма < 1; seed — целое 0–4 294 967 295.
 */
export interface DatasetSpecificationDto {
    name: string;
    description: string;
    groupIds: string[];
    classIds: string[];
    targetVariantIds: string[];
    negativeVariantIds: string[];
    unassignedVariantRole: DatasetUnassignedVariantRole;
    unselectedClassRole: DatasetUnselectedClassRole;
    includeReviewedEmpty: boolean;
    allowPartialGeneration: boolean;
    seed: number;
    valRatio: number;
    testRatio: number;
}

/** Готовые версии, новые первыми; includeArchived — «Показать архив датасетов» (по умолчанию true). */
export interface ListDatasetsRequestDto {
    projectId: string;
    includeArchived?: boolean;
}

export interface GetDatasetRequestDto {
    projectId: string;
    versionId: string;
}

/** Паспорт версии для моделей — и после её удаления (deletedAt). */
export interface GetDatasetPassportRequestDto {
    projectId: string;
    versionId: string;
}

/** «Изменить»: название 1–120, описание до 4000; revision — та, что видел пользователь. */
export interface UpdateDatasetRequestDto {
    projectId: string;
    versionId: string;
    name: string;
    description: string;
    revision: number;
}

/** «В архив» / «Восстановить». */
export interface SetDatasetArchivedRequestDto {
    projectId: string;
    versionId: string;
    archived: boolean;
    revision: number;
}

/** «Проверить перед сборкой»; operationId — новый на каждое нажатие (повтор запроса вернёт тот же план). */
export interface PlanDatasetRequestDto {
    projectId: string;
    specification: DatasetSpecificationDto;
    operationId: string;
}

export interface GetDatasetPlanRequestDto {
    projectId: string;
    planId: string;
}

/** «Собрать датасет»: plan.id и plan.hash из ответа plan; operationId — новый на действие. */
export interface StartDatasetBuildRequestDto {
    projectId: string;
    planId: string;
    planHash: string;
    operationId: string;
}

/** «Проверить целостность». */
export interface StartDatasetVerifyRequestDto {
    projectId: string;
    versionId: string;
    operationId: string;
}

/** «Экспорт ZIP», шаг 1: нативное окно «Сохранить как» с именем «<название>.zip» (перезапись подтверждается в окне). */
export interface SelectDatasetExportRequestDto {
    projectId: string;
    versionId: string;
}

/** «Экспорт ZIP», шаг 2: destinationToken из selectExportDestination (один токен — одна операция). */
export interface StartDatasetExportRequestDto {
    projectId: string;
    versionId: string;
    destinationToken: string;
    operationId: string;
}

/** Операции вида, новые первыми; limit 1–200 (по умолчанию 50), before — job.id последней операции предыдущей страницы. */
export interface ListDatasetOperationsRequestDto {
    projectId: string;
    kind: DatasetOperationKind;
    limit?: number;
    before?: string;
}

/** operationId — job.id операции (у сборки он же ID версии). */
export interface GetDatasetOperationRequestDto {
    projectId: string;
    operationId: string;
}

/** «Удалить из истории»: только завершённая операция (datasets.operation_active), версия остаётся. */
export interface DeleteDatasetOperationRequestDto {
    projectId: string;
    operationId: string;
}

/** Образцы части по ID; limit 1–200 (по умолчанию 100), after — nextCursor предыдущей страницы той же части. */
export interface ListDatasetSamplesRequestDto {
    projectId: string;
    versionId: string;
    split: DatasetSplit;
    limit?: number;
    after?: string;
}

/** Образец по ID (ID исходного изображения) — для просмотрщика при переходе ко всем образцам. */
export interface GetDatasetSampleRequestDto {
    projectId: string;
    versionId: string;
    sampleId: string;
}

export interface ReviewDatasetDeletionRequestDto {
    projectId: string;
    versionId: string;
}

/** «Удалить датасет» с revision из reviewDeletion. */
export interface DeleteDatasetRequestDto {
    projectId: string;
    versionId: string;
    revision: string;
}
