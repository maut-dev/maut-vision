import { nativeBridge } from "../transport/native-bridge";
import type { SampleHardwareRequestDto } from "./request-dto/sample-hardware-request-dto";
import { isHardwareSampleResponseDto } from "./response-dto/hardware-sample-response-dto";

/**
 * Нагрузка компьютера. Host ничего не опрашивает сам: каждый снимок — ответ на запрос. Страница спрашивает раз в ~2 с после
 * ответа и только пока раздел виден (useHardwareSample).
 */
export const hardwareApi = {
    sample(signal?: AbortSignal) {
        const request: SampleHardwareRequestDto = {};
        return nativeBridge.request("hardware.sample", request, isHardwareSampleResponseDto, signal);
    },
};
