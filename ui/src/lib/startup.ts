import { systemApi } from "@/api/system/system-api";
import { nativeBridge } from "@/api/transport/native-bridge";

let reported = false;

/**
 * Tells the host once per start that the library is shown: the host writes the startup time to the log
 * (perf.ps1 measures the time to a ready library by it). It is sent after a frame is painted.
 */
export function reportLibraryReady(projects: number): void {
    if (reported || !nativeBridge.available) return;
    reported = true;
    requestAnimationFrame(() => {
        systemApi.ready({ projects }).catch(() => {});
    });
}