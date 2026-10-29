import { useCallback, useEffect, useRef, useState } from "react";
import { baseModelsApi } from "@/api/base-models/base-models-api";
import type { BaseModelResponseDto } from "@/api/base-models/response-dto/base-model-response-dto";
import { subscribeBaseModelChanged } from "@/api/events/subscriptions";
import { i18n } from "@/i18n";
import { errorMessage } from "./format";

type Operation = "install" | "cancelInstallation" | "delete";

/** The base model catalog: the initial list by a request, later changes by baseModel.changed events. */
export function useBaseModels() {
    const [models, setModels] = useState<readonly BaseModelResponseDto[]>([]);
    const [loading, setLoading] = useState(true);
    const [error, setError] = useState<string>();
    const [pending, setPending] = useState<readonly string[]>([]);
    const mounted = useRef(true);

    const upsert = useCallback((model: BaseModelResponseDto) => {
        setModels(items => items.some(item => item.id === model.id)
            ? items.map(item => item.id === model.id ? model : item)
            : items);
    }, []);

    const refresh = useCallback(async () => {
        setLoading(true);
        try {
            const result = await baseModelsApi.list();
            if (!mounted.current) return;
            setModels(result);
            setError(undefined);
        } catch (cause) {
            if (mounted.current) setError(errorMessage(cause, i18n.t("baseModels.loadFailed")));
        } finally {
            if (mounted.current) setLoading(false);
        }
    }, []);

    useEffect(() => {
        mounted.current = true;
        const unsubscribe = subscribeBaseModelChanged(event => upsert(event.data));
        void refresh();
        return () => {
            mounted.current = false;
            unsubscribe();
        };
    }, [refresh, upsert]);

    async function run(modelId: string, operation: Operation) {
        setPending(items => [...items, modelId]);
        setError(undefined);
        try {
            upsert(await baseModelsApi[operation]({ modelId }));
        } catch (cause) {
            if (mounted.current) setError(errorMessage(cause));
        } finally {
            if (mounted.current) setPending(items => items.filter(id => id !== modelId));
        }
    }

    return {
        models,
        loading,
        error,
        pending,
        refresh,
        install: (modelId: string) => run(modelId, "install"),
        cancel: (modelId: string) => run(modelId, "cancelInstallation"),
        remove: (modelId: string) => run(modelId, "delete"),
    };
}

export type BaseModelsState = ReturnType<typeof useBaseModels>;
