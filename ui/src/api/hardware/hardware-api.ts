import { nativeBridge } from "../transport/native-bridge";
import type { SampleHardwareRequestDto } from "./request-dto/sample-hardware-request-dto";
import { isHardwareSampleResponseDto } from "./response-dto/hardware-sample-response-dto";

/**
 * The load of the computer. The host polls nothing by itself: every sample answers a request. The page asks every ~2 s after an
 * answer and only while the section is visible (useHardwareSample).
 */
export const hardwareApi = {
    sample(signal?: AbortSignal) {
        const request: SampleHardwareRequestDto = {};
        return nativeBridge.request("hardware.sample", request, isHardwareSampleResponseDto, signal);
    },
};
