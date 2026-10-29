import { useCallback, useEffect, useRef, useState } from "react";
import { subscribeTrainingPresetsChanged } from "@/api/events/subscriptions";
import type {
    GpuInventoryResponseDto,
    TrainingPresetResponseDto,
    TrainingRuntimeResponseDto,
} from "@/api/projects/training/response-dto/training-runtime-response-dto";
import { trainingApi } from "@/api/projects/training/training-api";
import { locale } from "@/lib/format";

/**
 * What the tab "Settings" needs besides the project: presets (shared by all projects; listPresets and trainingPresets.changed), the worker
 * environment (getRuntime — the code of why it is unavailable) and GPUs (listGpus for the field "Device"). Until an answer arrives the value
 * is undefined; a failed read of the environment or the GPUs does not block the form — Core checks the run itself.
 */
export function useTrainingSetup() {
    const [presets, setPresets] = useState<readonly TrainingPresetResponseDto[]>([]);
    const [runtime, setRuntime] = useState<TrainingRuntimeResponseDto | null>();
    const [gpus, setGpus] = useState<GpuInventoryResponseDto | null>();
    const mounted = useRef(true);
    const presetRead = useRef(0);

    const refreshPresets = useCallback(async () => {
        const own = ++presetRead.current;
        try {
            const list = await trainingApi.listPresets();
            if (mounted.current && own === presetRead.current) setPresets([...list].sort((left, right) => left.name.localeCompare(right.name, locale())));
        } catch {
            // Presets are optional: the form works without them.
        }
    }, []);

    useEffect(() => {
        mounted.current = true;
        const unsubscribe = subscribeTrainingPresetsChanged(() => void refreshPresets());
        void refreshPresets();
        trainingApi.getRuntime().then(value => { if (mounted.current) setRuntime(value); }, () => { if (mounted.current) setRuntime(null); });
        trainingApi.listGpus().then(value => { if (mounted.current) setGpus(value); }, () => { if (mounted.current) setGpus(null); });
        return () => {
            mounted.current = false;
            unsubscribe();
        };
    }, [refreshPresets]);

    return { presets, runtime, gpus, refreshPresets };
}
