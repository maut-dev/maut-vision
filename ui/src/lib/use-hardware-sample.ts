import { useEffect, useState } from "react";
import { hardwareApi } from "@/api/hardware/hardware-api";
import type { HardwareSampleResponseDto } from "@/api/hardware/response-dto/hardware-sample-response-dto";

export const hardwareSampleIntervalMs = 2_000;

/**
 * Снимок «Ваш компьютер», пока компонент смонтирован и страница видна. Следующий запрос уходит через 2 с после ответа (цепочка
 * setTimeout, без setInterval): медленный ответ не накапливает запросы. Свёрнутое окно (visibilityState hidden — host скрывает
 * WebView2) останавливает цепочку, возвращение окна сразу запрашивает новый снимок. Фокус окна не нужен. failed — последний
 * запрос не удался; sample тогда остаётся прежним (показатели могут быть устаревшими).
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
