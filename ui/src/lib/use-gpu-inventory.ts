import { useEffect, useState } from "react";
import type { GpuInventoryResponseDto } from "@/api/projects/training/response-dto/training-runtime-response-dto";
import { trainingApi } from "@/api/projects/training/training-api";

/** GPUs for the device field of checks: undefined while there is no answer yet, null when the list was not received (manual entry is available). */
export function useGpuInventory() {
    const [gpus, setGpus] = useState<GpuInventoryResponseDto | null>();
    useEffect(() => {
        let active = true;
        trainingApi.listGpus().then(value => { if (active) setGpus(value); }, () => { if (active) setGpus(null); });
        return () => { active = false; };
    }, []);
    return gpus;
}
