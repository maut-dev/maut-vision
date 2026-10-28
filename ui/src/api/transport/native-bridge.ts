import { isRecord, type EventEnvelope, type RequestEnvelope } from "./envelopes";
import { BridgeError } from "./errors";

interface WebView {
    postMessage(message: unknown): void;
    addEventListener(type: "message", listener: (event: { data: unknown }) => void): void;
    removeEventListener(type: "message", listener: (event: { data: unknown }) => void): void;
}

declare global {
    interface Window {
        chrome?: { webview?: WebView };
    }
}

/** details ошибки: объект строк или null; иное значение отбрасывается. */
function errorDetails(value: unknown): Readonly<Record<string, string>> | null {
    if (!isRecord(value)) return null;
    return Object.values(value).every(item => typeof item === "string") ? value as Record<string, string> : null;
}

interface Pending {
    resolve: (value: unknown) => void;
    reject: (error: Error) => void;
    cleanup: () => void;
}

export class NativeBridge {
    private readonly webview = window.chrome?.webview;
    private readonly pending = new Map<string, Pending>();
    private readonly subscribers = new Set<(event: EventEnvelope<unknown>) => void>();
    private disposed = false;

    constructor() {
        this.webview?.addEventListener("message", this.onMessage);
    }

    get available(): boolean {
        return this.webview !== undefined && !this.disposed;
    }

    subscribe(listener: (event: EventEnvelope<unknown>) => void): () => void {
        if (this.disposed) return () => {};
        this.subscribers.add(listener);
        return () => { this.subscribers.delete(listener); };
    }

    /**
     * timeoutMs — сколько ждать ответа (по умолчанию 10 с); null — без ограничения, для запросов, которые ждут
     * пользователя в нативном окне (выбор файлов). Таймаут не отменяет операцию в приложении.
     */
    request<TRequest, TResponse>(method: string, payload: TRequest, validate: (value: unknown) => value is TResponse, signal?: AbortSignal,
        timeoutMs: number | null = 10_000): Promise<TResponse> {
        const webview = this.webview;
        if (!webview || this.disposed) return Promise.reject(new BridgeError("bridge.unavailable"));
        if (signal?.aborted) return Promise.reject(new BridgeError("bridge.aborted"));
        return new Promise<TResponse>((resolve, reject) => {
            const requestId = crypto.randomUUID();
            const fail = (error: Error) => {
                const entry = this.pending.get(requestId);
                if (!entry) return;
                this.pending.delete(requestId);
                entry.cleanup();
                entry.reject(error);
            };
            const timer = timeoutMs === null ? undefined : window.setTimeout(() => fail(new BridgeError("bridge.timeout",
                { count: String(Math.round(timeoutMs / 1000)) })), timeoutMs);
            const abort = () => fail(new BridgeError("bridge.aborted"));
            this.pending.set(requestId, {
                resolve: value => {
                    if (validate(value)) resolve(value);
                    else reject(new BridgeError("bridge.invalid_response"));
                },
                reject,
                cleanup: () => {
                    window.clearTimeout(timer);
                    signal?.removeEventListener("abort", abort);
                },
            });
            signal?.addEventListener("abort", abort, { once: true });
            try {
                const request: RequestEnvelope<TRequest> = { requestId, method, payload };
                webview.postMessage(request);
            } catch {
                fail(new BridgeError("bridge.send_failed"));
            }
        });
    }

    private readonly onMessage = ({ data }: { data: unknown }): void => {
        if (!isRecord(data)) return;
        if (data.kind === "event") {
            if (typeof data.event !== "string" || !Object.hasOwn(data, "data")) return;
            const event: EventEnvelope<unknown> = { kind: "event", event: data.event, data: data.data };
            for (const listener of this.subscribers) {
                try { listener(event); }
                catch (error) { console.error("Native event subscriber failed.", error); }
            }
            return;
        }
        if (data.kind !== "response" || typeof data.requestId !== "string") return;
        const entry = this.pending.get(data.requestId);
        if (!entry) return;
        this.pending.delete(data.requestId);
        entry.cleanup();
        if (typeof data.ok !== "boolean") {
            entry.reject(new BridgeError("bridge.invalid_response"));
        } else if (!data.ok) {
            if (isRecord(data.error) && typeof data.error.code === "string") {
                entry.reject(new BridgeError(data.error.code, errorDetails(data.error.details)));
            } else entry.reject(new BridgeError("bridge.invalid_response"));
        } else entry.resolve(data.data);
    };

    dispose(): void {
        if (this.disposed) return;
        this.disposed = true;
        this.webview?.removeEventListener("message", this.onMessage);
        for (const entry of this.pending.values()) {
            entry.cleanup();
            entry.reject(new BridgeError("bridge.closed"));
        }
        this.pending.clear();
        this.subscribers.clear();
    }
}

export const nativeBridge = new NativeBridge();
window.addEventListener("pagehide", () => nativeBridge.dispose(), { once: true });
