import { nativeBridge } from "../transport/native-bridge";
import type { CancelJobRequestDto } from "./request-dto/cancel-job-request-dto";
import type { GetJobRequestDto } from "./request-dto/get-job-request-dto";
import type { ListActiveJobsRequestDto } from "./request-dto/list-active-jobs-request-dto";
import type { ListJobsRequestDto } from "./request-dto/list-jobs-request-dto";
import { isActiveJobsPageResponseDto } from "./response-dto/active-jobs-page-response-dto";
import { isJobResponseDto } from "./response-dto/job-response-dto";
import { isListJobsResponseDto } from "./response-dto/list-jobs-response-dto";

/**
 * Задачи открытого проекта. Задачи запускают методы конкретных возможностей; изменения приходят
 * событием job.changed, после переподключения состояние запрашивается заново.
 */
export const jobsApi = {
    get available(): boolean { return nativeBridge.available; },
    list(request: ListJobsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("jobs.list", request, isListJobsResponseDto, signal);
    },
    /** Активные задачи в порядке очереди страницами по 30 с точным total и числом по видам. */
    listActive(request: ListActiveJobsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("jobs.listActive", request, isActiveJobsPageResponseDto, signal);
    },
    get(request: GetJobRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("jobs.get", request, isJobResponseDto, signal);
    },
    cancel(request: CancelJobRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("jobs.cancel", request, isJobResponseDto, signal);
    },
};
