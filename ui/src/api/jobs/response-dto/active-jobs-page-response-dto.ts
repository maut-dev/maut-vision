import { isRecord } from "../../transport/envelopes";
import { isJobResponseDto, type JobResponseDto } from "./job-response-dto";

/** An active job: waitingForGpu — it waits in the queue for the GPU. */
export interface ActiveJobResponseDto {
    job: JobResponseDto;
    waitingForGpu: boolean;
}

/**
 * Active jobs of the project (queued, running, cancelling) in queue order. total and countsByKind count all active
 * jobs, not only the page; nextCursor is the after of the next page, null on the last one.
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
