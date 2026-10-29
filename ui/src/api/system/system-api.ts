import { nativeBridge } from "../transport/native-bridge";
import type { CloseDecisionRequestDto, CloseRequestDto } from "./request-dto/close-request-dto";
import type { LogRequestDto } from "./request-dto/log-request-dto";
import type { PingRequestDto } from "./request-dto/ping-request-dto";
import type { ReadyRequestDto } from "./request-dto/ready-request-dto";
import { isCloseResponseDto } from "./response-dto/close-response-dto";
import { isLogResponseDto } from "./response-dto/log-response-dto";
import { isPingResponseDto } from "./response-dto/ping-response-dto";
import { isReadyResponseDto } from "./response-dto/ready-response-dto";

export const systemApi = {
    ping(request: PingRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("system.ping", request, isPingResponseDto, signal);
    },
    log(request: LogRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("system.log", request, isLogResponseDto, signal);
    },
    ready(request: ReadyRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("system.ready", request, isReadyResponseDto, signal);
    },
    /** The answer to app.closeRequested: allow — there are no unsaved changes, the window closes. */
    closeDecision(request: CloseDecisionRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("system.closeDecision", request, isCloseResponseDto, signal);
    },
    /** Close the window after the user saved or discarded the changes. */
    close(signal?: AbortSignal) {
        const request: CloseRequestDto = {};
        return nativeBridge.request("system.close", request, isCloseResponseDto, signal);
    },
};
