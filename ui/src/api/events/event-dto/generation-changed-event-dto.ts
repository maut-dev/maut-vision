import { isId, isRevision } from "../../projects/catalog/catalog-guards";
import { isRecord } from "../../transport/envelopes";

/** Изменённый пресет с новой ревизией; revision = null — удалён. */
export interface GenerationEntityChangeDto {
    id: string;
    revision: number | null;
}

/**
 * Что изменилось в генерации проекта: recipes — пресеты с новыми ревизиями или удалённые; runs — запуски, у которых изменились данные
 * помимо состояния задачи (подготовлен — появилась группа результата; удалён): перечитать их getRun (generation.unavailable —
 * удалён). Состояние и прогресс запусков — job.changed.
 */
export interface GenerationChangedDto {
    projectId: string;
    recipes: GenerationEntityChangeDto[];
    runs: string[];
}

export interface GenerationChangedEventDto {
    kind: "event";
    event: "generation.changed";
    data: GenerationChangedDto;
}

function isChange(value: unknown): value is GenerationEntityChangeDto {
    return isRecord(value) && isId(value.id) && (value.revision === null || isRevision(value.revision));
}

export function isGenerationChangedDto(value: unknown): value is GenerationChangedDto {
    return isRecord(value) && typeof value.projectId === "string" && value.projectId.length > 0
        && Array.isArray(value.recipes) && value.recipes.every(isChange) && Array.isArray(value.runs) && value.runs.every(isId);
}
