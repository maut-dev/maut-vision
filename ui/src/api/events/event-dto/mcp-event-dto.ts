import { isRecord } from "../../transport/envelopes";

/** An AI client asked to show a project in the window (open_project_in_app). */
export interface McpOpenProjectEventData {
    projectId: string;
}

/** A confirmation was answered, timed out or the server stopped: its dialog closes. */
export interface McpConfirmationClosedEventData {
    id: string;
}

export function isMcpOpenProjectEventData(value: unknown): value is McpOpenProjectEventData {
    return isRecord(value) && typeof value.projectId === "string" && value.projectId.length > 0;
}

export function isMcpConfirmationClosedEventData(value: unknown): value is McpConfirmationClosedEventData {
    return isRecord(value) && typeof value.id === "string";
}
