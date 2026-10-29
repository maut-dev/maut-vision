import { isRecord } from "../../transport/envelopes";

/**
 * The user quits the application (Exit in the notification area; closing the window only hides it, a WPF window does not raise
 * beforeunload). The UI answers systemApi.closeDecision with this requestId within 1 s; without an answer the application exits.
 */
export interface CloseRequestedEventDto {
    kind: "event";
    event: "app.closeRequested";
    data: { requestId: string };
}

export function isCloseRequestedDto(value: unknown): value is CloseRequestedEventDto["data"] {
    return isRecord(value) && typeof value.requestId === "string" && value.requestId.length > 0;
}
