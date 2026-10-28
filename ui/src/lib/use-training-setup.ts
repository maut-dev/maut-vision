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
 * Что нужно вкладке «Настройки» помимо проекта: пресеты (общие для всех проектов; listPresets и trainingPresets.changed), среда
 * worker (getRuntime — причина недоступности по-русски) и видеокарты (listGpus для поля «Устройство»). Пока ответа нет, значение
 * undefined; ошибка чтения среды или видеокарт не блокирует форму — Core проверит запуск сам.
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
            // Пресеты необязательны: форма работает и без них.
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
