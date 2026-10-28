import { useCallback, useEffect, useRef, useState } from "react";
import { classesApi } from "@/api/projects/catalog/classes-api";
import type { ClassAssetResponseDto } from "@/api/projects/catalog/response-dto/class-asset-response-dto";
import { subscribeCatalogChanged } from "@/api/events/subscriptions";
import { errorMessage } from "./format";

const pageSize = 60;
const maxPage = 500;

interface AssetPages {
    items: readonly ClassAssetResponseDto[];
    nextCursor: string | null;
    loading: boolean;
    error?: string;
}

/**
 * Изображения одной привязки страницами по курсору. Перечитываются с начала, только когда catalog.changed
 * называет эту привязку в assetsChangedIn или URL миниатюры истёк; уже показанное число строк сохраняется.
 * Ответ устаревшего чтения (после него началось новое) отбрасывается.
 */
export function useClassAssets(projectId: string, classVariantId: string) {
    const [state, setState] = useState<AssetPages>({ items: [], nextCursor: null, loading: true });
    const generation = useRef(0);
    const shown = useRef(0);
    const mounted = useRef(true);
    const renewing = useRef(false);

    const reload = useCallback(async () => {
        const current = ++generation.current;
        setState(value => ({ ...value, loading: true }));
        try {
            const limit = Math.min(maxPage, Math.max(pageSize, shown.current));
            const page = await classesApi.listAssets({ projectId, classVariantId, limit });
            if (!mounted.current || current !== generation.current) return;
            shown.current = page.items.length;
            setState({ items: page.items, nextCursor: page.nextCursor, loading: false });
        } catch (cause) {
            if (mounted.current && current === generation.current) {
                setState(value => ({ ...value, loading: false, error: errorMessage(cause, "Не удалось загрузить изображения. Повторите загрузку.") }));
            }
        }
    }, [projectId, classVariantId]);

    const loadMore = useCallback(async () => {
        const after = state.nextCursor;
        if (!after || state.loading) return;
        const current = ++generation.current;
        setState(value => ({ ...value, loading: true }));
        try {
            const page = await classesApi.listAssets({ projectId, classVariantId, limit: pageSize, after });
            if (!mounted.current || current !== generation.current) return;
            setState(value => {
                const known = new Set(value.items.map(item => item.id));
                const items = [...value.items, ...page.items.filter(item => !known.has(item.id))];
                shown.current = items.length;
                return { items, nextCursor: page.nextCursor, loading: false };
            });
        } catch (cause) {
            if (mounted.current && current === generation.current) {
                setState(value => ({ ...value, loading: false, error: errorMessage(cause, "Не удалось загрузить изображения. Повторите загрузку.") }));
            }
        }
    }, [projectId, classVariantId, state.nextCursor, state.loading]);

    useEffect(() => {
        mounted.current = true;
        const unsubscribe = subscribeCatalogChanged(event => {
            if (event.data.projectId === projectId && event.data.assetsChangedIn.includes(classVariantId)) void reload();
        });
        void reload();
        return () => {
            mounted.current = false;
            unsubscribe();
        };
    }, [projectId, classVariantId, reload]);

    /** Истёкшие URL миниатюр: одно перечитывание на все изображения, сообщившие об ошибке одновременно. */
    const renew = useCallback(() => {
        if (renewing.current) return;
        renewing.current = true;
        void reload().finally(() => { renewing.current = false; });
    }, [reload]);

    return { ...state, reload, loadMore, renew };
}
