import { systemApi } from "@/api/system/system-api";
import { nativeBridge } from "@/api/transport/native-bridge";

let reported = false;

/**
 * Сообщает host один раз за запуск, что библиотека показана: host записывает время запуска в журнал
 * (по нему perf.ps1 измеряет время до готовности библиотеки). Отправляется после отрисовки кадра.
 */
export function reportLibraryReady(projects: number): void {
    if (reported || !nativeBridge.available) return;
    reported = true;
    requestAnimationFrame(() => {
        systemApi.ready({ projects }).catch(() => {});
    });
}