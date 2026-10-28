import { isId } from "../../projects/catalog/catalog-guards";
import { isLiveCheckResponseDto, type LiveCheckResponseDto } from "../../projects/checks/response-dto/live-check-response-dto";
import { isRecord } from "../../transport/envelopes";

/** Оценка с новой ревизией; revision = null — удалена (вместе с версией модели). */
export interface EvaluationChangeDto {
    id: string;
    revision: number | null;
}

/**
 * Что изменилось в проверках проекта: evaluations — перечитать checksApi.getEvaluation, если revision больше известной; check — снимок
 * живой проверки без журнала (recentFrames пусто), не чаще 5 раз в секунду, переходы состояний — сразу; применять, только если
 * check.sequence больше известного.
 */
export interface ChecksChangedDto {
    projectId: string;
    evaluations: EvaluationChangeDto[];
    check: LiveCheckResponseDto | null;
}

export interface ChecksChangedEventDto {
    kind: "event";
    event: "checks.changed";
    data: ChecksChangedDto;
}

export function isChecksChangedDto(value: unknown): value is ChecksChangedDto {
    return isRecord(value) && typeof value.projectId === "string" && value.projectId.length > 0 && Array.isArray(value.evaluations)
        && value.evaluations.every(item => isRecord(item) && isId(item.id) && (item.revision === null || (Number.isSafeInteger(item.revision) && (item.revision as number) >= 0)))
        && (value.check === null || isLiveCheckResponseDto(value.check));
}
