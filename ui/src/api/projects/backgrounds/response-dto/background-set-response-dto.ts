import { isRecord } from "../../../transport/envelopes";
import { isCount, isDate, isDateOrNull, isId, isRevision } from "../../catalog/catalog-guards";
import { isInputSizeDto, type InputSizeDto } from "../../input-size-dto";

/**
 * Набор фонов: «N фонов · W × H[ · В архиве]» — itemCount (активные) и targetSize. matchesProjectInput — false, если вход
 * проекта изменён после создания: «Размер набора W × H отличается от входа проекта» (такие наборы не подойдут генерации
 * нового размера). revision — для updateSet и setSetArchived; contentRevision растёт при каждом изменении состава;
 * removedCount — убранные фоны. Подготовка меняет только счётчики (backgrounds.changed → itemsChangedIn).
 */
export interface BackgroundSetResponseDto {
    id: string;
    name: string;
    description: string;
    targetSize: InputSizeDto;
    revision: number;
    contentRevision: number;
    itemCount: number;
    removedCount: number;
    createdAt: string;
    updatedAt: string;
    archivedAt: string | null;
    matchesProjectInput: boolean;
}

export function isBackgroundSetResponseDto(value: unknown): value is BackgroundSetResponseDto {
    return isRecord(value) && isId(value.id) && typeof value.name === "string" && typeof value.description === "string"
        && isInputSizeDto(value.targetSize) && isRevision(value.revision) && isRevision(value.contentRevision)
        && isCount(value.itemCount) && isCount(value.removedCount) && isDate(value.createdAt) && isDate(value.updatedAt)
        && isDateOrNull(value.archivedAt) && typeof value.matchesProjectInput === "boolean";
}

export function isBackgroundSetList(value: unknown): value is BackgroundSetResponseDto[] {
    return Array.isArray(value) && value.every(isBackgroundSetResponseDto);
}
