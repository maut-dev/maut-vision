import { isActiveJobsPageResponseDto, type ActiveJobsPageResponseDto } from "../../../jobs/response-dto/active-jobs-page-response-dto";
import { isRecord } from "../../../transport/envelopes";

/** ready — neither archived nor corrupt; archived — in the archive; corrupt — corrupt outside the archive. */
export interface OverviewDatasetsDto {
    ready: number;
    archived: number;
    corrupt: number;
}

/** A row of the latest models: runId leads to the training run, metrics are the keys of the version (mAP50, mAP50_95 and others). */
export interface OverviewModelVersionDto {
    id: string;
    number: number;
    isWorking: boolean;
    createdAt: string;
    architecture: string;
    metrics: Record<string, number>;
    runId: string;
}

/**
 * The project overview (overview.get): the project data counters, up to 5 latest model versions (newest first) and the first page of
 * active jobs. It is refreshed by job.changed, datasets.changed, training.changed and checks.changed.
 */
export interface OverviewResponseDto {
    projectId: string;
    capturedAt: string;
    datasets: OverviewDatasetsDto;
    modelVersionCount: number;
    latestVersions: OverviewModelVersionDto[];
    activeJobs: ActiveJobsPageResponseDto;
}

const isCount = (value: unknown) => Number.isSafeInteger(value) && (value as number) >= 0;

const isDate = (value: unknown) => typeof value === "string" && Number.isFinite(Date.parse(value));

const isId = (value: unknown) => typeof value === "string" && value.length > 0;

function isOverviewModelVersionDto(value: unknown): value is OverviewModelVersionDto {
    return isRecord(value) && isId(value.id) && isCount(value.number) && typeof value.isWorking === "boolean" && isDate(value.createdAt)
        && typeof value.architecture === "string" && isRecord(value.metrics) && Object.values(value.metrics).every(item => typeof item === "number")
        && isId(value.runId);
}

export function isOverviewResponseDto(value: unknown): value is OverviewResponseDto {
    return isRecord(value) && isId(value.projectId) && isDate(value.capturedAt)
        && isRecord(value.datasets) && isCount(value.datasets.ready) && isCount(value.datasets.archived) && isCount(value.datasets.corrupt)
        && isCount(value.modelVersionCount) && Array.isArray(value.latestVersions) && value.latestVersions.every(isOverviewModelVersionDto)
        && isActiveJobsPageResponseDto(value.activeJobs);
}
