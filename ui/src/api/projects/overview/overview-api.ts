import { nativeBridge } from "../../transport/native-bridge";
import type { GetOverviewRequestDto } from "./request-dto/get-overview-request-dto";
import { isOverviewResponseDto } from "./response-dto/overview-response-dto";

/** Обзор открытого проекта одним запросом; следующие страницы активных задач — jobsApi.listActive. */
export const overviewApi = {
    get(request: GetOverviewRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("overview.get", request, isOverviewResponseDto, signal);
    },
};
