import { useCallback, useEffect, useRef, useState } from "react";
import { datasetsApi } from "@/api/projects/datasets/datasets-api";
import type { DatasetSplit } from "@/api/projects/datasets/request-dto/dataset-request-dto";
import type { DatasetSampleResponseDto } from "@/api/projects/datasets/response-dto/dataset-response-dto";
import { errorMessage } from "./format";

export const samplePageLimit = 100;

/**
 * Образцы одной части версии постранично (datasets.listSamples с курсором). Версия неизменяема, поэтому событий нет: страницы
 * читаются по мере прокрутки сетки или перехода просмотрщика за последний загруженный образец (loadMore возвращает, удалось ли
 * дочитать). Смена версии или части начинает новое поколение — ответы прежней отбрасываются. renew перечитывает образец, если
 * URL его изображения истёк.
 */
export function useDatasetSamples(projectId: string, versionId: string, split: DatasetSplit) {
    const [items, setItems] = useState<readonly DatasetSampleResponseDto[]>([]);
    const [cursor, setCursor] = useState<string | null>(null);
    const [started, setStarted] = useState(false);
    const [loading, setLoading] = useState(false);
    const [error, setError] = useState<string>();
    const generation = useRef(0);
    const pending = useRef<Promise<boolean> | null>(null);
    const state = useRef({ cursor, started });
    state.current = { cursor, started };

    const loadMore = useCallback((): Promise<boolean> => {
        if (pending.current) return pending.current;
        const { cursor: after, started: read } = state.current;
        if (read && !after) return Promise.resolve(false);
        const current = generation.current;
        setLoading(true);
        setError(undefined);
        const work = (async () => {
            try {
                const page = await datasetsApi.listSamples({ projectId, versionId, split, limit: samplePageLimit, ...(after ? { after } : {}) });
                if (current !== generation.current) return false;
                setItems(known => [...known, ...page.items.filter(item => !known.some(entry => entry.id === item.id))]);
                setCursor(page.nextCursor);
                setStarted(true);
                state.current = { cursor: page.nextCursor, started: true };
                return page.items.length > 0;
            } catch (cause) {
                if (current === generation.current) setError(errorMessage(cause, "Не удалось загрузить примеры. Повторите загрузку."));
                return false;
            } finally {
                if (current === generation.current) {
                    pending.current = null;
                    setLoading(false);
                }
            }
        })();
        pending.current = work;
        return work;
    }, [projectId, versionId, split]);

    useEffect(() => {
        generation.current++;
        pending.current = null;
        state.current = { cursor: null, started: false };
        setItems([]);
        setCursor(null);
        setStarted(false);
        setLoading(false);
        setError(undefined);
        void loadMore();
        return () => { generation.current++; };
    }, [loadMore]);

    const renew = useCallback(async (sampleId: string) => {
        const current = generation.current;
        try {
            const sample = await datasetsApi.getSample({ projectId, versionId, sampleId });
            if (current === generation.current) setItems(known => known.map(item => item.id === sample.id ? sample : item));
        } catch {
            // The tile shows its failure state; a retry reloads the page.
        }
    }, [projectId, versionId]);

    return { items, hasMore: !started || cursor !== null, loading, error, loadMore, renew };
}

export type DatasetSamplesState = ReturnType<typeof useDatasetSamples>;
