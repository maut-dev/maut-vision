import { nativeBridge } from "../../transport/native-bridge";
import type { GetOverviewRequestDto } from "./request-dto/get-overview-request-dto";
import { isOverviewResponseDto } from "./response-dto/overview-response-dto";

/** The overview of the open project in one request; the next pages of active jobs come from jobsApi.listActive. */
export const overviewApi = {
    get(request: GetOverviewRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("overview.get", request, isOverviewResponseDto, signal);
    },
};
