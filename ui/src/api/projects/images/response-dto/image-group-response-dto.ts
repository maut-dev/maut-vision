import { isRecord } from "../../../transport/envelopes";
import { isCount, isDate, isDateOrNull, isId, isRevision } from "../../catalog/catalog-guards";

/** Назначение: «Без назначения / Обучение / Проверка / Тест». */
export type ImageSplitHint = "unassigned" | "train" | "validation" | "test";

export type ImageGroupOrigin = "manual" | "generated";

export type ImageGenerationState = "producing" | "completed" | "partial";

export const imageSplitHints: readonly ImageSplitHint[] = ["unassigned", "train", "validation", "test"];

const origins: readonly string[] = ["manual", "generated"] satisfies ImageGroupOrigin[];
const generationStates: readonly string[] = ["producing", "completed", "partial"] satisfies ImageGenerationState[];

/**
 * Группа изображений. imageCount — все изображения, включая архивные; archivedImageCount — архивные («Кадров: N»).
 * revision растёт при изменении полей группы, архиве и перемещении изображений; импорт меняет только счётчики (их
 * изменение приходит в images.changed → imagesChangedIn). generationState — только у сгенерированных групп;
 * при producing группа только для чтения: «Генерация наполняет эту группу. Редактирование станет доступно после завершения.»
 */
export interface ImageGroupResponseDto {
    id: string;
    name: string;
    description: string;
    origin: ImageGroupOrigin;
    splitHint: ImageSplitHint;
    revision: number;
    createdAt: string;
    updatedAt: string;
    archivedAt: string | null;
    generationRunId: string | null;
    generationState: ImageGenerationState | null;
    imageCount: number;
    archivedImageCount: number;
}

export function isImageGroupResponseDto(value: unknown): value is ImageGroupResponseDto {
    return isRecord(value) && isId(value.id) && typeof value.name === "string" && typeof value.description === "string"
        && typeof value.origin === "string" && origins.includes(value.origin)
        && typeof value.splitHint === "string" && (imageSplitHints as readonly string[]).includes(value.splitHint)
        && isRevision(value.revision) && isDate(value.createdAt) && isDate(value.updatedAt) && isDateOrNull(value.archivedAt)
        && (value.generationRunId === null || isId(value.generationRunId))
        && (value.generationState === null || (typeof value.generationState === "string" && generationStates.includes(value.generationState)))
        && isCount(value.imageCount) && isCount(value.archivedImageCount);
}

export function isImageGroupList(value: unknown): value is ImageGroupResponseDto[] {
    return Array.isArray(value) && value.every(isImageGroupResponseDto);
}
