import { nativeBridge } from "../transport/native-bridge";
import type { GetLanguageRequestDto, SetLanguageRequestDto } from "./request-dto/language-request-dto";
import { isLanguageResponseDto } from "./response-dto/language-response-dto";

export const settingsApi = {
    /** The interface language kept by the host (host-settings.json). */
    getLanguage(signal?: AbortSignal, timeoutMs?: number) {
        const request: GetLanguageRequestDto = {};
        return nativeBridge.request("settings.getLanguage", request, isLanguageResponseDto, signal, timeoutMs);
    },
    /** Stores the setting; the host relabels its tray and sends settings.languageChanged. */
    setLanguage(request: SetLanguageRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("settings.setLanguage", request, isLanguageResponseDto, signal);
    },
};