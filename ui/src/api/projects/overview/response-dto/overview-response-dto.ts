import { isActiveJobsPageResponseDto, type ActiveJobsPageResponseDto } from "../../../jobs/response-dto/active-jobs-page-response-dto";
import { isRecord } from "../../../transport/envelopes";

/** ready — не в архиве и не повреждённые; archived — в архиве; corrupt — повреждённые вне архива. */
export interface OverviewDatasetsDto {
    ready: number;
    archived: number;
    corrupt: number;
}

/** Строка «Последние модели»: runId ведёт к запуску обучения, metrics — ключи версии (mAP50, mAP50_95 и др.). */
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
 * Обзор проекта (overview.get): счётчики «Данные проекта», до 5 последних версий моделей (новые первыми) и первая страница
 * активных задач. Обновляется по job.changed, datasets.changed, training.changed и checks.changed.
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
