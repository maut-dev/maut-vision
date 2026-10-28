import { useCallback, useEffect, useRef, useState } from "react";
import { subscribeDatasetsChanged } from "@/api/events/subscriptions";
import { datasetsApi } from "@/api/projects/datasets/datasets-api";
import type { DatasetVersionResponseDto } from "@/api/projects/datasets/response-dto/dataset-response-dto";
import { BridgeError } from "@/api/transport/errors";
import { errorMessage } from "./format";

const newestFirst = (left: DatasetVersionResponseDto, right: DatasetVersionResponseDto) =>
    right.createdAt.localeCompare(left.createdAt) || right.id.localeCompare(left.id);

/**
 * Готовые версии датасетов открытого проекта, включая архивные (фильтр «Показать архив датасетов» — на экране): datasets.list
 * при открытии, затем datasets.changed. Перечитываются только названные событием версии с ревизией новее известной; null —
 * версия удалена. Ответ операции (update, setArchived) применяется сразу; более старая ревизия не заменяет новую.
 */
export function useDatasetVersions(projectId: string) {
    const [versions, setVersions] = useState<readonly DatasetVersionResponseDto[]>([]);
    const [loading, setLoading] = useState(true);
    const [error, setError] = useState<string>();
    const mounted = useRef(true);
    const latest = useRef(versions);
    latest.current = versions;
    const reads = useRef(new Map<string, number>());
    const sequence = useRef(0);

    const apply = useCallback((version: DatasetVersionResponseDto) => {
        setVersions(current => {
            const known = current.find(item => item.id === version.id);
            if (known && known.revision > version.revision) return current;
            return known ? current.map(item => item.id === version.id ? version : item) : [...current, version].sort(newestFirst);
        });
    }, []);

    const remove = useCallback((versionId: string) => {
        reads.current.delete(versionId);
        setVersions(current => current.filter(item => item.id !== versionId));
    }, []);

    const reread = useCallback(async (versionId: string) => {
        const read = ++sequence.current;
        reads.current.set(versionId, read);
        try {
            const version = await datasetsApi.get({ projectId, versionId });
            if (mounted.current && reads.current.get(versionId) === read) apply(version);
        } catch (cause) {
            if (mounted.current && reads.current.get(versionId) === read && cause instanceof BridgeError && cause.code === "datasets.unavailable") remove(versionId);
        }
    }, [projectId, apply, remove]);

    const refresh = useCallback(async () => {
        setLoading(true);
        try {
            const list = await datasetsApi.list({ projectId, includeArchived: true });
            if (!mounted.current) return;
            setVersions([...list].sort(newestFirst));
            setError(undefined);
        } catch (cause) {
            if (mounted.current) setError(errorMessage(cause, "Не удалось загрузить датасеты. Повторите загрузку."));
        } finally {
            if (mounted.current) setLoading(false);
        }
    }, [projectId]);

    useEffect(() => {
        mounted.current = true;
        const unsubscribe = subscribeDatasetsChanged(event => {
            if (event.data.projectId !== projectId) return;
            for (const change of event.data.versions) {
                if (change.revision === null) remove(change.id);
                else if ((latest.current.find(item => item.id === change.id)?.revision ?? 0) < change.revision) void reread(change.id);
            }
        });
        void refresh();
        return () => {
            mounted.current = false;
            unsubscribe();
        };
    }, [projectId, refresh, reread, remove]);

    return { versions, loading, error, refresh, apply, remove };
}

export type DatasetVersionsState = ReturnType<typeof useDatasetVersions>;
