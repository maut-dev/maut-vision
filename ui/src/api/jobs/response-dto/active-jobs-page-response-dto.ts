import { isRecord } from "../../transport/envelopes";
import { isJobResponseDto, type JobResponseDto } from "./job-response-dto";

/** Активная задача: waitingForGpu — стоит в очереди за видеокартой («Ожидает видеокарту»). */
export interface ActiveJobResponseDto {
    job: JobResponseDto;
    waitingForGpu: boolean;
}

/**
 * Активные задачи проекта (в очереди, выполняются, отменяются) в порядке очереди. total и countsByKind считают все активные
 * задачи, не только страницу; nextCursor — after следующей страницы, null на последней.
 */
export interface ActiveJobsPageResponseDto {
    items: ActiveJobResponseDto[];
    total: number;
    countsByKind: Record<string, number>;
    nextCursor: string | null;
}

const isCount = (value: unknown) => Number.isSafeInteger(value) && (value as number) >= 0;

function isActiveJobResponseDto(value: unknown): value is ActiveJobResponseDto {
    return isRecord(value) && isJobResponseDto(value.job) && typeof value.waitingForGpu === "boolean";
}

export function isActiveJobsPageResponseDto(value: unknown): value is ActiveJobsPageResponseDto {
    return isRecord(value) && Array.isArray(value.items) && value.items.every(isActiveJobResponseDto)
        && isCount(value.total)
        && isRecord(value.countsByKind) && Object.values(value.countsByKind).every(isCount)
        && (value.nextCursor === null || (typeof value.nextCursor === "string" && value.nextCursor.length > 0));
}
