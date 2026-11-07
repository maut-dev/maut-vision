import type { WindowStateResponseDto } from "../../window/response-dto/window-state-response-dto";

/** The main window was maximized, restored, minimized, activated or deactivated (also by Win+Up, snapping or the drag area). */
export interface WindowStateChangedEventDto {
    kind: "event";
    event: "window.stateChanged";
    data: WindowStateResponseDto;
}
