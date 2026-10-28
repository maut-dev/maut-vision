import { useCallback, useEffect, useRef, useState } from "react";
import { subscribeTrainingChanged } from "@/api/events/subscriptions";
import type { ModelVersionResponseDto } from "@/api/projects/training/response-dto/model-version-response-dto";
import { trainingApi } from "@/api/projects/training/training-api";
import { errorMessage } from "./format";

/**
 * Версии моделей открытого проекта: listModelVersions при открытии и после training.changed с изменёнными версиями (появилась
 * новая, сменилась «Рабочая», удалена — revision = null). renew — свежие ссылки на картинки (ссылки канала ресурсов действуют ограниченное время).
 */
export function useModelVersions(projectId: string) {
    const [versions, setVersions] = useState<readonly ModelVersionResponseDto[]>([]);
    const [error, setError] = useState<string>();
    /** Список хотя бы раз прочитан: версия, которой в нём нет, удалена. */
    const [loaded, setLoaded] = useState(false);
    /** Время начала последнего прочитанного списка: версия, созданная позже, могла в него не попасть. */
    const [listedAt, setListedAt] = useState(0);
    const mounted = useRef(true);
    const read = useRef(0);

    const refresh = useCallback(async () => {
        const own = ++read.current;
        const startedAt = Date.now();
        try {
            const list = await trainingApi.listModelVersions({ projectId });
            if (!mounted.current || own !== read.current) return;
            setVersions(list);
            setLoaded(true);
            setListedAt(startedAt);
            setError(undefined);
        } catch (cause) {
            if (mounted.current && own === read.current) setError(errorMessage(cause, "Не удалось загрузить версии модели."));
        }
    }, [projectId]);

    const apply = useCallback((version: ModelVersionResponseDto) => {
        setVersions(current => current.some(item => item.id === version.id)
            ? current.map(item => item.id === version.id ? version : item.isWorking && version.isWorking ? { ...item, isWorking: false } : item)
            : [...current, version]);
    }, []);

    /** Ссылки на картинки версии истекли: перечитать её. */
    const renew = useCallback(async (versionId: string) => {
        try {
            const version = await trainingApi.getModelVersion({ projectId, versionId });
            if (mounted.current) apply(version);
        } catch {
            // Версия недоступна — список обновит следующее событие.
        }
    }, [projectId, apply]);

    useEffect(() => {
        mounted.current = true;
        const unsubscribe = subscribeTrainingChanged(event => {
            if (event.data.projectId === projectId && event.data.versions.length) void refresh();
        });
        void refresh();
        return () => {
            mounted.current = false;
            unsubscribe();
        };
    }, [projectId, refresh]);

    /** «Сделать рабочей». */
    const setWorking = useCallback(async (versionId: string) => {
        apply(await trainingApi.setWorkingModelVersion({ projectId, versionId }));
    }, [projectId, apply]);

    return { versions, loaded, listedAt, error, refresh, renew, setWorking };
}
