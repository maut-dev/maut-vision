import { nativeBridge } from "../transport/native-bridge";
import type { WindowCommandRequestDto } from "./request-dto/window-request-dto";
import { isWindowStateResponseDto } from "./response-dto/window-state-response-dto";

const empty: WindowCommandRequestDto = {};

/** The window buttons of the title bar: the window has no system caption, the page draws it. */
export const windowApi = {
    getState(signal?: AbortSignal) {
        return nativeBridge.request("window.getState", empty, isWindowStateResponseDto, signal);
    },
    minimize(signal?: AbortSignal) {
        return nativeBridge.request("window.minimize", empty, isWindowStateResponseDto, signal);
    },
    /** Maximizes a normal window and restores a maximized one. */
    toggleMaximize(signal?: AbortSignal) {
        return nativeBridge.request("window.toggleMaximize", empty, isWindowStateResponseDto, signal);
    },
    /** The close button: the window hides in the notification area and the application keeps running (exit is in the tray). */
    close(signal?: AbortSignal) {
        return nativeBridge.request("window.close", empty, isWindowStateResponseDto, signal);
    },
};
