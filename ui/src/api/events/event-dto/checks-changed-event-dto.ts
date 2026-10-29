import { isId } from "../../projects/catalog/catalog-guards";
import { isLiveCheckResponseDto, type LiveCheckResponseDto } from "../../projects/checks/response-dto/live-check-response-dto";
import { isRecord } from "../../transport/envelopes";

/** An evaluation with its new revision; revision = null when it was deleted (together with the model version). */
export interface EvaluationChangeDto {
    id: string;
    revision: number | null;
}

/**
 * What changed in the checks of the project: evaluations — read checksApi.getEvaluation again when the revision is greater than the known
 * one; check — a snapshot of a live check without the journal (recentFrames is empty), at most 5 times a second, state changes at once;
 * apply it only when check.sequence is greater than the known one.
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
