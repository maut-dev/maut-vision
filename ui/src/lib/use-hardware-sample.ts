import { useEffect, useState } from "react";
import { hardwareApi } from "@/api/hardware/hardware-api";
import type { HardwareSampleResponseDto } from "@/api/hardware/response-dto/hardware-sample-response-dto";

export const hardwareSampleIntervalMs = 2_000;

/**
 * A sample of the computer while the component is mounted and the page is visible. The next request goes 2 s after the answer (a chain
 * of setTimeout, no setInterval): a slow answer does not pile up requests. A minimized window (visibilityState hidden — the host hides
 * WebView2) stops the chain, a returning window asks for a new sample at once. Window focus is not needed. failed — the last
 * request failed; sample then stays as it was (the readings may be out of date).
 */
export function useHardwareSample() {
    const [sample, setSample] = useState<HardwareSampleResponseDto>();
    const [failed, setFailed] = useState(false);

    useEffect(() => {
        let mounted = true;
        let timer: number | undefined;
        let controller: AbortController | undefined;

        const stop = () => {
            window.clearTimeout(timer);
            timer = undefined;
            controller?.abort();
            controller = undefined;
        };
        const request = () => {
            timer = undefined;
            if (!mounted || document.visibilityState !== "visible" || controller) return;
            const current = new AbortController();
            controller = current;
            hardwareApi.sample(current.signal).then(value => {
                if (!mounted || current.signal.aborted) return;
                setSample(value);
                setFailed(false);
            }, () => {
                if (!mounted || current.signal.aborted) return;
                setFailed(true);
            }).finally(() => {
                if (controller !== current) return;
                controller = undefined;
                if (mounted && document.visibilityState === "visible") timer = window.setTimeout(request, hardwareSampleIntervalMs);
            });
        };
        const onVisibility = () => {
            if (document.visibilityState === "visible") {
                if (timer === undefined && !controller) request();
            } else {
                stop();
            }
        };

        document.addEventListener("visibilitychange", onVisibility);
        request();
        return () => {
            mounted = false;
            document.removeEventListener("visibilitychange", onVisibility);
            stop();
        };
    }, []);

    return { sample, failed };
}
