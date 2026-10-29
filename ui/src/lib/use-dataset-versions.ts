import { useCallback, useEffect, useRef, useState } from "react";
import { subscribeDatasetsChanged } from "@/api/events/subscriptions";
import { datasetsApi } from "@/api/projects/datasets/datasets-api";
import type { DatasetVersionResponseDto } from "@/api/projects/datasets/response-dto/dataset-response-dto";
import { BridgeError } from "@/api/transport/errors";
import { i18n } from "@/i18n";
import { errorMessage } from "./format";
import "./datasets/locale";

const newestFirst = (left: DatasetVersionResponseDto, right: DatasetVersionResponseDto) =>
    right.createdAt.localeCompare(left.createdAt) || right.id.localeCompare(left.id);

/**
 * Finished dataset versions of the open project, archived ones too (the filter "Show dataset archive" is on the screen): datasets.list
 * on opening, then datasets.changed. Only the versions named by the event with a revision newer than known are read again; null —
 * the version was deleted. The answer of an operation (update, setArchived) applies at once; an older revision does not replace a newer one.
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
            if (mounted.current) setError(errorMessage(cause, i18n.t("datasets:loadErrors.versions")));
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
