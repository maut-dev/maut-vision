import { nativeBridge } from "../transport/native-bridge";
import type { CancelJobRequestDto } from "./request-dto/cancel-job-request-dto";
import type { GetJobRequestDto } from "./request-dto/get-job-request-dto";
import type { ListActiveJobsRequestDto } from "./request-dto/list-active-jobs-request-dto";
import type { ListJobsRequestDto } from "./request-dto/list-jobs-request-dto";
import { isActiveJobsPageResponseDto } from "./response-dto/active-jobs-page-response-dto";
import { isJobResponseDto } from "./response-dto/job-response-dto";
import { isListJobsResponseDto } from "./response-dto/list-jobs-response-dto";

/**
 * Jobs of the open project. Jobs are started by the methods of each feature; changes come with
 * the job.changed event, after a reconnection the state is requested again.
 */
export const jobsApi = {
    get available(): boolean { return nativeBridge.available; },
    list(request: ListJobsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("jobs.list", request, isListJobsResponseDto, signal);
    },
    /** Active jobs in queue order, in pages of 30, with the exact total and the counts by kind. */
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
