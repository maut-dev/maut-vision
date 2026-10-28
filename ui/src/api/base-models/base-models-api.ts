import { nativeBridge } from "../transport/native-bridge";
import type { CancelBaseModelInstallationRequestDto } from "./request-dto/cancel-base-model-installation-request-dto";
import type { DeleteBaseModelRequestDto } from "./request-dto/delete-base-model-request-dto";
import type { InstallBaseModelRequestDto } from "./request-dto/install-base-model-request-dto";
import type { ListBaseModelsRequestDto } from "./request-dto/list-base-models-request-dto";
import { isBaseModelResponseDto } from "./response-dto/base-model-response-dto";
import { isListBaseModelsResponseDto } from "./response-dto/list-base-models-response-dto";

/** Установка возвращает состояние сразу; прогресс и результат приходят событием baseModel.changed. */
export const baseModelsApi = {
    get available(): boolean { return nativeBridge.available; },
    list(signal?: AbortSignal) {
        const request: ListBaseModelsRequestDto = {};
        return nativeBridge.request("baseModels.list", request, isListBaseModelsResponseDto, signal);
    },
    install(request: InstallBaseModelRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("baseModels.install", request, isBaseModelResponseDto, signal);
    },
    cancelInstallation(request: CancelBaseModelInstallationRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("baseModels.cancelInstallation", request, isBaseModelResponseDto, signal);
    },
    delete(request: DeleteBaseModelRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("baseModels.delete", request, isBaseModelResponseDto, signal);
    },
};
