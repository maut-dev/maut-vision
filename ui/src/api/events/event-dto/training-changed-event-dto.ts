import { isId } from "../../projects/catalog/catalog-guards";
import { isRecord } from "../../transport/envelopes";

/** Изменённый запуск или версия с новой ревизией; revision = null — удалены. */
export interface TrainingEntityChangeDto {
    id: string;
    revision: number | null;
}

/**
 * Что изменилось в обучении проекта: runs — запуски (ход, эпохи, журнал, фактические параметры, итог; не чаще раза в 500 мс):
 * перечитать getRun, если revision больше известной, затем listEpochs / listLog с after; versions — версии моделей (появилась,
 * сменилась «Рабочая», удалена — revision = null вместе с её оценками): перечитать listModelVersions. Состояние и прогресс задачи — job.changed (kind training.run).
 */
export interface TrainingChangedDto {
    projectId: string;
    runs: TrainingEntityChangeDto[];
    versions: TrainingEntityChangeDto[];
}

export interface TrainingChangedEventDto {
    kind: "event";
    event: "training.changed";
    data: TrainingChangedDto;
}

/** Пресеты обучения сохранены или удалены: перечитать listPresets. */
export interface TrainingPresetsChangedDto {
    presetIds: string[];
}

export interface TrainingPresetsChangedEventDto {
    kind: "event";
    event: "trainingPresets.changed";
    data: TrainingPresetsChangedDto;
}

const isChange = (item: unknown) => isRecord(item) && isId(item.id) && (item.revision === null || (Number.isSafeInteger(item.revision) && (item.revision as number) >= 0));

export function isTrainingChangedDto(value: unknown): value is TrainingChangedDto {
    return isRecord(value) && typeof value.projectId === "string" && value.projectId.length > 0 && Array.isArray(value.runs) && value.runs.every(isChange)
        && Array.isArray(value.versions) && value.versions.every(isChange);
}

export function isTrainingPresetsChangedDto(value: unknown): value is TrainingPresetsChangedDto {
    return isRecord(value) && Array.isArray(value.presetIds) && value.presetIds.every(isId);
}
