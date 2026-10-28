export interface RequestEnvelope<T> {
    requestId: string;
    method: string;
    payload: T;
}

export type ResponseEnvelope<T> = {
    kind: "response";
    requestId: string;
    ok: true;
    data: T;
} | {
    kind: "response";
    requestId: string;
    ok: false;
    error: { code: string; details?: Readonly<Record<string, string>> | null };
};

export interface EventEnvelope<T> {
    kind: "event";
    event: string;
    data: T;
}

export function isRecord(value: unknown): value is Record<string, unknown> {
    return typeof value === "object" && value !== null && !Array.isArray(value);
}
