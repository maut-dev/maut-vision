import { maxLogMessageLength, maxLogStackLength } from "@/api/system/request-dto/log-request-dto";
import { systemApi } from "@/api/system/system-api";
import { nativeBridge } from "@/api/transport/native-bridge";

// The host limits the rate as well; this keeps a failing render loop from flooding the bridge.
const maxReportsPerMinute = 20;
let windowStart = 0;
let reportsInWindow = 0;

/** Passes unhandled errors and rejected promises to the host log. */
export function installErrorReporting(): void {
    window.addEventListener("error", event => report(event.error ?? event.message, ""));
    window.addEventListener("unhandledrejection", event => report(event.reason, "Unhandled rejection: "));
}

function report(reason: unknown, prefix: string): void {
    try {
        if (!nativeBridge.available) return;
        const now = Date.now();
        if (now - windowStart >= 60_000) {
            windowStart = now;
            reportsInWindow = 0;
        }
        if (++reportsInWindow > maxReportsPerMinute) return;
        const error = reason instanceof Error ? reason : undefined;
        const text = error ? `${error.name}: ${error.message}` : String(reason);
        const message = (prefix + text).slice(0, maxLogMessageLength) || "Unknown error";
        const stack = error?.stack?.slice(0, maxLogStackLength);
        // A failed report is dropped: reporting it again would loop.
        systemApi.log(stack ? { level: "error", message, stack } : { level: "error", message }).catch(() => {});
    } catch {
        // Reporting never raises a new error.
    }
}
