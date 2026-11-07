import { isRecord } from "../../transport/envelopes";

/**
 * The state of the main window: maximized — the title bar offers «Восстановить»; active — the window has the focus (inactive window
 * buttons are dimmed). Also the event window.stateChanged.
 */
export interface WindowStateResponseDto {
    maximized: boolean;
    active: boolean;
}

export function isWindowStateResponseDto(value: unknown): value is WindowStateResponseDto {
    return isRecord(value) && typeof value.maximized === "boolean" && typeof value.active === "boolean";
}
