import { useEffect, useState } from "react";
import type { GpuInventoryResponseDto } from "@/api/projects/training/response-dto/training-runtime-response-dto";
import { trainingApi } from "@/api/projects/training/training-api";

/** Видеокарты для поля «Устройство» проверок: undefined — ответа ещё нет, null — список не получен (доступен ручной ввод). */
export function useGpuInventory() {
    const [gpus, setGpus] = useState<GpuInventoryResponseDto | null>();
    useEffect(() => {
        let active = true;
        trainingApi.listGpus().then(value => { if (active) setGpus(value); }, () => { if (active) setGpus(null); });
        return () => { active = false; };
    }, []);
    return gpus;
}
